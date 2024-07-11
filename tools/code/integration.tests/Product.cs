using Azure.Core.Pipeline;
using common;
using common.tests;
using CsCheck;
using FluentAssertions;
using LanguageExt;
using LanguageExt.UnsafeValueAccess;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
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

public delegate ValueTask DeleteAllProducts(ManagementServiceName serviceName, CancellationToken cancellationToken);
public delegate ValueTask PutProductModels(IEnumerable<ProductModel> models, ManagementServiceName serviceName, CancellationToken cancellationToken);
public delegate ValueTask ValidateExtractedProducts(Option<FrozenSet<ProductName>> productNamesOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);
public delegate ValueTask<FrozenDictionary<ProductName, ProductDto>> GetApimProducts(ManagementServiceName serviceName, CancellationToken cancellationToken);
public delegate ValueTask<FrozenDictionary<ProductName, ProductDto>> GetFileProducts(ManagementServiceDirectory serviceDirectory, Option<CommitId> commitIdOption, CancellationToken cancellationToken);
public delegate ValueTask WriteProductModels(IEnumerable<ProductModel> models, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);
public delegate ValueTask ValidatePublishedProducts(IDictionary<ProductName, ProductDto> overrides, Option<CommitId> commitIdOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

public static class ProductModule
{
    public static void ConfigureDeleteAllProducts(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureGetManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteAllProducts);
    }

    private static DeleteAllProducts GetDeleteAllProducts(IServiceProvider provider)
    {
        var getServiceUri = provider.GetRequiredService<GetManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (serviceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteAllProducts));

            logger.LogInformation("Deleting all products in {ServiceName}...", serviceName);

            var serviceUri = getServiceUri(serviceName);

            await ProductsUri.From(serviceUri)
                             .DeleteAll(pipeline, cancellationToken);
        };
    }

    public static void ConfigurePutProductModels(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureGetManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutProductModels);
    }

    private static PutProductModels GetPutProductModels(IServiceProvider provider)
    {
        var getServiceUri = provider.GetRequiredService<GetManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (models, serviceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutProductModels));

            logger.LogInformation("Putting product models in {ServiceName}...", serviceName);

            await models.IterParallel(async model =>
            {
                await put(model, serviceName, cancellationToken);
            }, cancellationToken);
        };

        async ValueTask put(ProductModel model, ManagementServiceName serviceName, CancellationToken cancellationToken)
        {
            var serviceUri = getServiceUri(serviceName);

            var dto = getDto(model);

            await ProductUri.From(model.Name, serviceUri)
                            .PutDto(dto, pipeline, cancellationToken);
        }

        static ProductDto getDto(ProductModel model) =>
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

    public static void ConfigureValidateExtractedProducts(IHostApplicationBuilder builder)
    {
        ConfigureGetApimProducts(builder);
        ConfigureGetFileProducts(builder);

        builder.Services.TryAddSingleton(GetValidateExtractedProducts);
    }

    private static ValidateExtractedProducts GetValidateExtractedProducts(IServiceProvider provider)
    {
        var getApimResources = provider.GetRequiredService<GetApimProducts>();
        var tryGetApimGraphQlSchema = provider.GetRequiredService<TryGetApimGraphQlSchema>();
        var getFileResources = provider.GetRequiredService<GetFileProducts>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (namesFilterOption, serviceName, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ValidateExtractedProducts));

            logger.LogInformation("Validating extracted products in {ServiceName}...", serviceName);

            var apimResources = await getApimResources(serviceName, cancellationToken);
            var fileResources = await getFileResources(serviceDirectory, Prelude.None, cancellationToken);

            var expected = apimResources.WhereKey(name => ExtractorOptions.ShouldExtract(name, namesFilterOption))
                                        .MapValue(normalizeDto)
                                        .ToFrozenDictionary();

            var actual = fileResources.MapValue(normalizeDto)
                                      .ToFrozenDictionary();

            actual.Should().BeEquivalentTo(expected);
        };

        static string normalizeDto(ProductDto dto) =>
            new
            {
                DisplayName = dto.Properties.DisplayName ?? string.Empty,
                Description = dto.Properties.Description ?? string.Empty,
                Terms = dto.Properties.Terms ?? string.Empty,
                State = dto.Properties.State ?? string.Empty
            }.ToString()!;
    }

    public static void ConfigureGetApimProducts(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureGetManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetGetApimProducts);
    }

    private static GetApimProducts GetGetApimProducts(IServiceProvider provider)
    {
        var getServiceUri = provider.GetRequiredService<GetManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (serviceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(GetApimProducts));

            logger.LogInformation("Getting products from {ServiceName}...", serviceName);

            var serviceUri = getServiceUri(serviceName);

            return await ProductsUri.From(serviceUri)
                                    .List(pipeline, cancellationToken)
                                    .ToFrozenDictionary(cancellationToken);
        };
    }

    public static void ConfigureGetFileProducts(IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(GetGetFileProducts);
    }

    private static GetFileProducts GetGetFileProducts(IServiceProvider provider)
    {
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (serviceDirectory, commitIdOption, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(GetFileProducts));

            return await commitIdOption.Map(commitId => getWithCommit(serviceDirectory, commitId, cancellationToken))
                                       .IfNone(() => getWithoutCommit(serviceDirectory, cancellationToken));
        };

        async ValueTask<FrozenDictionary<ProductName, ProductDto>> getWithCommit(ManagementServiceDirectory serviceDirectory, CommitId commitId, CancellationToken cancellationToken)
        {
            using var _ = activitySource.StartActivity(nameof(GetFileProducts));

            logger.LogInformation("Getting products from {ServiceDirectory} as of commit {CommitId}...", serviceDirectory, commitId);

            return await Git.GetExistingFilesInCommit(serviceDirectory.ToDirectoryInfo(), commitId)
                            .ToAsyncEnumerable()
                            .Choose(file => ProductInformationFile.TryParse(file, serviceDirectory))
                            .Choose(async file => await tryGetCommitResource(commitId, serviceDirectory, file, cancellationToken))
                            .ToFrozenDictionary(cancellationToken);
        }

        static async ValueTask<Option<(ProductName name, ProductDto dto)>> tryGetCommitResource(CommitId commitId, ManagementServiceDirectory serviceDirectory, ProductInformationFile file, CancellationToken cancellationToken)
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

        async ValueTask<FrozenDictionary<ProductName, ProductDto>> getWithoutCommit(ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
        {
            logger.LogInformation("Getting products from {ServiceDirectory}...", serviceDirectory);

            return await common.ProductModule.ListInformationFiles(serviceDirectory)
                                             .ToAsyncEnumerable()
                                             .SelectAwait(async file => (file.Parent.Name,
                                                                         await file.ReadDto(cancellationToken)))
                                             .ToFrozenDictionary(cancellationToken);
        }
    }

    public static void ConfigureWriteProductModels(IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(GetWriteProductModels);
    }

    private static WriteProductModels GetWriteProductModels(IServiceProvider provider)
    {
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (models, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(WriteProductModels));

            logger.LogInformation("Writing product models to {ServiceDirectory}...", serviceDirectory);

            await models.IterParallel(async model =>
            {
                await writeInformationFile(model, serviceDirectory, cancellationToken);
            }, cancellationToken);
        };

        static async ValueTask writeInformationFile(ProductModel model, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
        {
            var informationFile = ProductInformationFile.From(model.Name, serviceDirectory);
            var dto = getDto(model);

            await informationFile.WriteDto(dto, cancellationToken);
        }

        static ProductDto getDto(ProductModel model) =>
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

    public static void ConfigureValidatePublishedProducts(IHostApplicationBuilder builder)
    {
        ConfigureGetFileProducts(builder);
        ConfigureGetApimProducts(builder);

        builder.Services.TryAddSingleton(GetValidatePublishedProducts);
    }

    private static ValidatePublishedProducts GetValidatePublishedProducts(IServiceProvider provider)
    {
        var getFileResources = provider.GetRequiredService<GetFileProducts>();
        var getApimResources = provider.GetRequiredService<GetApimProducts>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (overrides, commitIdOption, serviceName, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ValidatePublishedProducts));

            logger.LogInformation("Validating published products in {ServiceDirectory}...", serviceDirectory);

            var apimResources = await getApimResources(serviceName, cancellationToken);
            var fileResources = await getFileResources(serviceDirectory, commitIdOption, cancellationToken);

            var expected = PublisherOptions.Override(fileResources, overrides)
                                           .MapValue(normalizeDto)
                                           .ToFrozenDictionary();

            var actual = apimResources.MapValue(normalizeDto)
                                      .ToFrozenDictionary();

            actual.Should().BeEquivalentTo(expected);
        };

        static string normalizeDto(ProductDto dto) =>
            new
            {
                DisplayName = dto.Properties.DisplayName ?? string.Empty,
                Description = dto.Properties.Description ?? string.Empty,
                Terms = dto.Properties.Terms ?? string.Empty,
                State = dto.Properties.State ?? string.Empty
            }.ToString()!;
    }

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