using Azure.Core.Pipeline;
using common;
using common.tests;
using CsCheck;
using FluentAssertions;
using LanguageExt;
using LanguageExt.UnsafeValueAccess;
using publisher;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace integration.tests;

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

    public static async ValueTask Put(IEnumerable<ProductModel> models, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await models.IterParallel(async model =>
        {
            await Put(model, serviceUri, pipeline, cancellationToken);
        }, cancellationToken);

    private static async ValueTask Put(ProductModel model, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var uri = ProductUri.From(model.Name, serviceUri);
        var dto = GetDto(model);

        await uri.PutDto(dto, pipeline, cancellationToken);

        await ProductPolicy.Put(model.Policies, model.Name, serviceUri, pipeline, cancellationToken);
        await ProductGroup.Put(model.Groups, model.Name, serviceUri, pipeline, cancellationToken);
        await ProductTag.Put(model.Tags, model.Name, serviceUri, pipeline, cancellationToken);
        await ProductApi.Put(model.Apis, model.Name, serviceUri, pipeline, cancellationToken);
    }

    public static async ValueTask DeleteAll(ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await ProductsUri.From(serviceUri).DeleteAll(pipeline, cancellationToken);

    public static async ValueTask WriteArtifacts(IEnumerable<ProductModel> models, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await models.IterParallel(async model =>
        {
            await WriteInformationFile(model, serviceDirectory, cancellationToken);

            await ProductPolicy.WriteArtifacts(model.Policies, model.Name, serviceDirectory, cancellationToken);
            await ProductGroup.WriteArtifacts(model.Groups, model.Name, serviceDirectory, cancellationToken);
            await ProductTag.WriteArtifacts(model.Tags, model.Name, serviceDirectory, cancellationToken);
            await ProductApi.WriteArtifacts(model.Apis, model.Name, serviceDirectory, cancellationToken);
        }, cancellationToken);

    private static async ValueTask WriteInformationFile(ProductModel model, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        var informationFile = ProductInformationFile.From(model.Name, serviceDirectory);
        var dto = GetDto(model);

        await informationFile.WriteDto(dto, cancellationToken);
    }

    public static async ValueTask ValidateExtractedArtifacts(Option<FrozenSet<ProductName>> namesToExtract, ManagementServiceDirectory serviceDirectory, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var apimResources = await GetApimResources(serviceUri, pipeline, cancellationToken);
        var fileResources = await GetFileResources(serviceDirectory, cancellationToken);

        var expected = apimResources.WhereKey(name => ExtractorOptions.ShouldExtract(name, namesToExtract))
                                    .MapValue(NormalizeDto);
        var actual = fileResources.MapValue(NormalizeDto);

        actual.Should().BeEquivalentTo(expected);

        await expected.Keys.IterParallel(async productName =>
        {
            await ProductPolicy.ValidateExtractedArtifacts(serviceDirectory, productName, serviceUri, pipeline, cancellationToken);
            await ProductGroup.ValidateExtractedArtifacts(serviceDirectory, productName, serviceUri, pipeline, cancellationToken);
            await ProductTag.ValidateExtractedArtifacts(serviceDirectory, productName, serviceUri, pipeline, cancellationToken);
            await ProductApi.ValidateExtractedArtifacts(serviceDirectory, productName, serviceUri, pipeline, cancellationToken);
        }, cancellationToken);
    }

    private static async ValueTask<FrozenDictionary<ProductName, ProductDto>> GetApimResources(ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var uri = ProductsUri.From(serviceUri);

        return await uri.List(pipeline, cancellationToken)
                        .ToFrozenDictionary(cancellationToken);
    }

    private static async ValueTask<FrozenDictionary<ProductName, ProductDto>> GetFileResources(ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await ProductModule.ListInformationFiles(serviceDirectory)
                              .ToAsyncEnumerable()
                              .SelectAwait(async file => (file.Parent.Name,
                                                          await file.ReadDto(cancellationToken)))
                              .ToFrozenDictionary(cancellationToken);

    private static string NormalizeDto(ProductDto dto) =>
        new
        {
            DisplayName = dto.Properties.DisplayName ?? string.Empty,
            Description = dto.Properties.Description ?? string.Empty,
            Terms = dto.Properties.Terms ?? string.Empty,
            State = dto.Properties.State ?? string.Empty
        }.ToString()!;

    public static async ValueTask ValidatePublisherChanges(ManagementServiceDirectory serviceDirectory, IDictionary<ProductName, ProductDto> overrides, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var fileResources = await GetFileResources(serviceDirectory, cancellationToken);
        await ValidatePublisherChanges(fileResources, overrides, serviceUri, pipeline, cancellationToken);

        await fileResources.Keys.IterParallel(async productName =>
        {
            await ProductPolicy.ValidatePublisherChanges(productName, serviceDirectory, serviceUri, pipeline, cancellationToken);
            await ProductGroup.ValidatePublisherChanges(productName, serviceDirectory, serviceUri, pipeline, cancellationToken);
            await ProductTag.ValidatePublisherChanges(productName, serviceDirectory, serviceUri, pipeline, cancellationToken);
            await ProductApi.ValidatePublisherChanges(productName, serviceDirectory, serviceUri, pipeline, cancellationToken);
        }, cancellationToken);
    }

    private static async ValueTask ValidatePublisherChanges(IDictionary<ProductName, ProductDto> fileResources, IDictionary<ProductName, ProductDto> overrides, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var apimResources = await GetApimResources(serviceUri, pipeline, cancellationToken);

        var expected = PublisherOptions.Override(fileResources, overrides)
                                       .MapValue(NormalizeDto);
        var actual = apimResources.MapValue(NormalizeDto);
        actual.Should().BeEquivalentTo(expected);
    }

    public static async ValueTask ValidatePublisherCommitChanges(CommitId commitId, ManagementServiceDirectory serviceDirectory, IDictionary<ProductName, ProductDto> overrides, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var fileResources = await GetFileResources(commitId, serviceDirectory, cancellationToken);
        await ValidatePublisherChanges(fileResources, overrides, serviceUri, pipeline, cancellationToken);

        await fileResources.Keys.IterParallel(async productName =>
        {
            await ProductPolicy.ValidatePublisherCommitChanges(productName, commitId, serviceDirectory, serviceUri, pipeline, cancellationToken);
            await ProductGroup.ValidatePublisherCommitChanges(productName, commitId, serviceDirectory, serviceUri, pipeline, cancellationToken);
            await ProductTag.ValidatePublisherCommitChanges(productName, commitId, serviceDirectory, serviceUri, pipeline, cancellationToken);
            await ProductApi.ValidatePublisherCommitChanges(productName, commitId, serviceDirectory, serviceUri, pipeline, cancellationToken);
        }, cancellationToken);
    }

    private static async ValueTask<FrozenDictionary<ProductName, ProductDto>> GetFileResources(CommitId commitId, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await Git.GetExistingFilesInCommit(serviceDirectory.ToDirectoryInfo(), commitId)
                 .ToAsyncEnumerable()
                 .Choose(file => ProductInformationFile.TryParse(file, serviceDirectory))
                 .Choose(async file => await TryGetCommitResource(commitId, serviceDirectory, file, cancellationToken))
                 .ToFrozenDictionary(cancellationToken);

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
}
