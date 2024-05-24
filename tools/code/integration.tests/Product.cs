using Azure.Core.Pipeline;
using common;
using common.tests;
using CsCheck;
using FluentAssertions;
using LanguageExt;
using LanguageExt.UnsafeValueAccess;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using publisher;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace integration.tests;

internal delegate ValueTask DeleteAllProducts(ManagementServiceName serviceName, CancellationToken cancellationToken);

internal delegate ValueTask PutProductModels(IEnumerable<ProductModel> models, ManagementServiceName serviceName, CancellationToken cancellationToken);

internal delegate ValueTask ValidateExtractedProducts(Option<FrozenSet<ProductName>> namesFilterOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

file delegate ValueTask<FrozenDictionary<ProductName, ProductDto>> GetApimProducts(ManagementServiceName serviceName, CancellationToken cancellationToken);

file delegate ValueTask<FrozenDictionary<ProductName, ProductDto>> GetFileProducts(ManagementServiceDirectory serviceDirectory, Option<CommitId> commitIdOption, CancellationToken cancellationToken);

internal delegate ValueTask WriteProductModels(IEnumerable<ProductModel> models, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

internal delegate ValueTask ValidatePublishedProducts(IDictionary<ProductName, ProductDto> overrides, Option<CommitId> commitIdOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

file sealed class DeleteAllProductsHandler(ILogger<DeleteAllProducts> logger, GetManagementServiceUri getServiceUri, HttpPipeline pipeline, ActivitySource activitySource)
{
    public async ValueTask Handle(ManagementServiceName serviceName, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(DeleteAllProducts));

        logger.LogInformation("Deleting all products in {ServiceName}...", serviceName);
        var serviceUri = getServiceUri(serviceName);
        await ProductsUri.From(serviceUri).DeleteAll(pipeline, cancellationToken);
    }
}

file sealed class PutProductModelsHandler(ILogger<PutProductModels> logger, GetManagementServiceUri getServiceUri, HttpPipeline pipeline, ActivitySource activitySource)
{
    public async ValueTask Handle(IEnumerable<ProductModel> models, ManagementServiceName serviceName, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(PutProductModels));

        logger.LogInformation("Putting product models in {ServiceName}...", serviceName);
        await models.IterParallel(async model =>
        {
            await Put(model, serviceName, cancellationToken);
        }, cancellationToken);
    }

    private async ValueTask Put(ProductModel model, ManagementServiceName serviceName, CancellationToken cancellationToken)
    {
        var serviceUri = getServiceUri(serviceName);
        var uri = ProductUri.From(model.Name, serviceUri);
        var dto = GetDto(model);

        await uri.PutDto(dto, pipeline, cancellationToken);
    }

    private static ProductDto GetDto(ProductModel model) =>
        new()
        {
            Properties = new ProductDto.ProductContract
            {
                DisplayName = model.DisplayName,
                Description = model.Description.ValueUnsafe(),
                Terms = model.Terms.ValueUnsafe(),
                State = model.State
            }
        };
}

file sealed class ValidateExtractedProductsHandler(ILogger<ValidateExtractedProducts> logger, GetApimProducts getApimResources, GetFileProducts getFileResources, ActivitySource activitySource)
{
    public async ValueTask Handle(Option<FrozenSet<ProductName>> namesFilterOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(ValidateExtractedProducts));

        logger.LogInformation("Validating extracted products in {ServiceName}...", serviceName);
        var apimResources = await getApimResources(serviceName, cancellationToken);
        var fileResources = await getFileResources(serviceDirectory, Prelude.None, cancellationToken);

        var expected = apimResources.WhereKey(name => ExtractorOptions.ShouldExtract(name, namesFilterOption))
                                    .MapValue(NormalizeDto);
        var actual = fileResources.MapValue(NormalizeDto);

        actual.Should().BeEquivalentTo(expected);
    }

    private static string NormalizeDto(ProductDto dto) =>
        new
        {
            DisplayName = dto.Properties.DisplayName ?? string.Empty,
            Description = dto.Properties.Description ?? string.Empty,
            Terms = dto.Properties.Terms ?? string.Empty,
            State = dto.Properties.State ?? string.Empty
        }.ToString()!;
}

file sealed class GetApimProductsHandler(ILogger<GetApimProducts> logger, GetManagementServiceUri getServiceUri, HttpPipeline pipeline, ActivitySource activitySource)
{
    public async ValueTask<FrozenDictionary<ProductName, ProductDto>> Handle(ManagementServiceName serviceName, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(GetApimProducts));

        logger.LogInformation("Getting products from {ServiceName}...", serviceName);

        var serviceUri = getServiceUri(serviceName);
        var uri = ProductsUri.From(serviceUri);

        return await uri.List(pipeline, cancellationToken)
                        .ToFrozenDictionary(cancellationToken);
    }
}

file sealed class GetFileProductsHandler(ILogger<GetFileProducts> logger, ActivitySource activitySource)
{
    public async ValueTask<FrozenDictionary<ProductName, ProductDto>> Handle(ManagementServiceDirectory serviceDirectory, Option<CommitId> commitIdOption, CancellationToken cancellationToken) =>
        await commitIdOption.Map(commitId => GetWithCommit(serviceDirectory, commitId, cancellationToken))
                           .IfNone(() => GetWithoutCommit(serviceDirectory, cancellationToken));

    private async ValueTask<FrozenDictionary<ProductName, ProductDto>> GetWithCommit(ManagementServiceDirectory serviceDirectory, CommitId commitId, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(GetFileProducts));

        logger.LogInformation("Getting products from {ServiceDirectory} as of commit {CommitId}...", serviceDirectory, commitId);

        return await Git.GetExistingFilesInCommit(serviceDirectory.ToDirectoryInfo(), commitId)
                        .ToAsyncEnumerable()
                        .Choose(file => ProductInformationFile.TryParse(file, serviceDirectory))
                        .Choose(async file => await TryGetCommitResource(commitId, serviceDirectory, file, cancellationToken))
                        .ToFrozenDictionary(cancellationToken);
    }

    private static async ValueTask<Option<(ProductName name, ProductDto dto)>> TryGetCommitResource(CommitId commitId, ManagementServiceDirectory serviceDirectory, ProductInformationFile file, CancellationToken cancellationToken)
    {
        var name = file.Parent.Name;
        var contentsOption = Git.TryGetFileContentsInCommit(serviceDirectory.ToDirectoryInfo(), file.ToFileInfo(), commitId);

        return await contentsOption.MapTask(async contents =>
        {
            using (contents)
            {
                var data = await BinaryData.FromStreamAsync(contents, cancellationToken);
                var dto = data.ToObjectFromJson<ProductDto>();
                return (name, dto);
            }
        });
    }

    private async ValueTask<FrozenDictionary<ProductName, ProductDto>> GetWithoutCommit(ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(GetFileProducts));

        logger.LogInformation("Getting products from {ServiceDirectory}...", serviceDirectory);

        return await ProductModule.ListInformationFiles(serviceDirectory)
                              .ToAsyncEnumerable()
                              .SelectAwait(async file => (file.Parent.Name,
                                                          await file.ReadDto(cancellationToken)))
                              .ToFrozenDictionary(cancellationToken);
    }
}

file sealed class WriteProductModelsHandler(ILogger<WriteProductModels> logger, ActivitySource activitySource)
{
    public async ValueTask Handle(IEnumerable<ProductModel> models, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(WriteProductModels));

        logger.LogInformation("Writing product models to {ServiceDirectory}...", serviceDirectory);
        await models.IterParallel(async model =>
        {
            await WriteInformationFile(model, serviceDirectory, cancellationToken);
        }, cancellationToken);
    }

    private static async ValueTask WriteInformationFile(ProductModel model, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        var informationFile = ProductInformationFile.From(model.Name, serviceDirectory);
        var dto = GetDto(model);

        await informationFile.WriteDto(dto, cancellationToken);
    }

    private static ProductDto GetDto(ProductModel model) =>
        new()
        {
            Properties = new ProductDto.ProductContract
            {
                DisplayName = model.DisplayName,
                Description = model.Description.ValueUnsafe(),
                Terms = model.Terms.ValueUnsafe(),
                State = model.State
            }
        };
}

file sealed class ValidatePublishedProductsHandler(ILogger<ValidatePublishedProducts> logger, GetFileProducts getFileResources, GetApimProducts getApimResources, ActivitySource activitySource)
{
    public async ValueTask Handle(IDictionary<ProductName, ProductDto> overrides, Option<CommitId> commitIdOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(ValidatePublishedProducts));

        logger.LogInformation("Validating published products in {ServiceDirectory}...", serviceDirectory);

        var apimResources = await getApimResources(serviceName, cancellationToken);
        var fileResources = await getFileResources(serviceDirectory, commitIdOption, cancellationToken);

        var expected = PublisherOptions.Override(fileResources, overrides)
                                       .MapValue(NormalizeDto);
        var actual = apimResources.MapValue(NormalizeDto);

        actual.Should().BeEquivalentTo(expected);
    }

    private static string NormalizeDto(ProductDto dto) =>
        new
        {
            DisplayName = dto.Properties.DisplayName ?? string.Empty,
            Description = dto.Properties.Description ?? string.Empty,
            Terms = dto.Properties.Terms ?? string.Empty,
            State = dto.Properties.State ?? string.Empty
        }.ToString()!;
}

internal static class ProductServices
{
    public static void ConfigureDeleteAllProducts(IServiceCollection services)
    {
        ManagementServices.ConfigureGetManagementServiceUri(services);

        services.TryAddSingleton<DeleteAllProductsHandler>();
        services.TryAddSingleton<DeleteAllProducts>(provider => provider.GetRequiredService<DeleteAllProductsHandler>().Handle);
    }

    public static void ConfigurePutProductModels(IServiceCollection services)
    {
        ManagementServices.ConfigureGetManagementServiceUri(services);

        services.TryAddSingleton<PutProductModelsHandler>();
        services.TryAddSingleton<PutProductModels>(provider => provider.GetRequiredService<PutProductModelsHandler>().Handle);
    }

    public static void ConfigureValidateExtractedProducts(IServiceCollection services)
    {
        ConfigureGetApimProducts(services);
        ConfigureGetFileProducts(services);

        services.TryAddSingleton<ValidateExtractedProductsHandler>();
        services.TryAddSingleton<ValidateExtractedProducts>(provider => provider.GetRequiredService<ValidateExtractedProductsHandler>().Handle);
    }

    private static void ConfigureGetApimProducts(IServiceCollection services)
    {
        ManagementServices.ConfigureGetManagementServiceUri(services);

        services.TryAddSingleton<GetApimProductsHandler>();
        services.TryAddSingleton<GetApimProducts>(provider => provider.GetRequiredService<GetApimProductsHandler>().Handle);
    }

    private static void ConfigureGetFileProducts(IServiceCollection services)
    {
        services.TryAddSingleton<GetFileProductsHandler>();
        services.TryAddSingleton<GetFileProducts>(provider => provider.GetRequiredService<GetFileProductsHandler>().Handle);
    }

    public static void ConfigureWriteProductModels(IServiceCollection services)
    {
        services.TryAddSingleton<WriteProductModelsHandler>();
        services.TryAddSingleton<WriteProductModels>(provider => provider.GetRequiredService<WriteProductModelsHandler>().Handle);
    }

    public static void ConfigureValidatePublishedProducts(IServiceCollection services)
    {
        ConfigureGetFileProducts(services);
        ConfigureGetApimProducts(services);

        services.TryAddSingleton<ValidatePublishedProductsHandler>();
        services.TryAddSingleton<ValidatePublishedProducts>(provider => provider.GetRequiredService<ValidatePublishedProductsHandler>().Handle);
    }
}

internal static class Product
{
    public static Gen<ProductModel> GenerateUpdate(ProductModel original) =>
        from displayName in ProductModel.GenerateDisplayName()
        from description in ProductModel.GenerateDescription().OptionOf()
        from terms in ProductModel.GenerateTerms().OptionOf()
        from state in ProductModel.GenerateState()
        select original with
        {
            DisplayName = displayName,
            Description = description,
            Terms = terms,
            State = state
        };

    public static Gen<ProductDto> GenerateOverride(ProductDto original) =>
        from displayName in ProductModel.GenerateDisplayName()
        from description in ProductModel.GenerateDescription().OptionOf()
        from terms in ProductModel.GenerateTerms().OptionOf()
        from state in ProductModel.GenerateState()
        select new ProductDto
        {
            Properties = new ProductDto.ProductContract
            {
                DisplayName = displayName,
                Description = description.ValueUnsafe(),
                Terms = terms.ValueUnsafe(),
                State = state
            }
        };

    public static FrozenDictionary<ProductName, ProductDto> GetDtoDictionary(IEnumerable<ProductModel> models) =>
        models.ToFrozenDictionary(model => model.Name, GetDto);

    private static ProductDto GetDto(ProductModel model) =>
        new()
        {
            Properties = new ProductDto.ProductContract
            {
                DisplayName = model.DisplayName,
                Description = model.Description.ValueUnsafe(),
                Terms = model.Terms.ValueUnsafe(),
                State = model.State
            }
        };
}
