using Azure.Core.Pipeline;
using common;
using common.tests;
using FluentAssertions;
using LanguageExt;
using publisher;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace integration.tests;

internal static class ProductGroupModule
{
    public static async ValueTask Put(IEnumerable<ProductGroupModel> models, ProductName productName, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await models.IterParallel(async model =>
        {
            await Put(model, productName, serviceUri, pipeline, cancellationToken);
        }, cancellationToken);

    private static async ValueTask Put(ProductGroupModel model, ProductName productName, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var uri = ProductGroupUri.From(model.Name, productName, serviceUri);

        await uri.PutDto(ProductGroupDto.Instance, pipeline, cancellationToken);
    }

    public static async ValueTask WriteArtifacts(IEnumerable<ProductGroupModel> models, ProductName productName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await models.IterParallel(async model =>
        {
            await WriteInformationFile(model, productName, serviceDirectory, cancellationToken);
        }, cancellationToken);

    private static async ValueTask WriteInformationFile(ProductGroupModel model, ProductName productName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        var informationFile = ProductGroupInformationFile.From(model.Name, productName, serviceDirectory);
        await informationFile.WriteDto(ProductGroupDto.Instance, cancellationToken);
    }

    public static async ValueTask ValidateExtractedArtifacts(ManagementServiceDirectory serviceDirectory, ProductName productName, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var productmResources = await GetProductmResources(productName, serviceUri, pipeline, cancellationToken);
        var fileResources = await GetFileResources(productName, serviceDirectory, cancellationToken);

        var expected = productmResources.MapValue(NormalizeDto);
        var actual = fileResources.MapValue(NormalizeDto);

        actual.Should().BeEquivalentTo(expected);
    }

    private static async ValueTask<FrozenDictionary<GroupName, ProductGroupDto>> GetProductmResources(ProductName productName, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var uri = ProductGroupsUri.From(productName, serviceUri);

        return await uri.List(pipeline, cancellationToken)
                        .ToFrozenDictionary(cancellationToken);
    }

    private static async ValueTask<FrozenDictionary<GroupName, ProductGroupDto>> GetFileResources(ProductName productName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await common.ProductGroupModule.ListInformationFiles(productName, serviceDirectory)
                                 .ToAsyncEnumerable()
                                 .SelectAwait(async file => (file.Parent.Name,
                                                             await file.ReadDto(cancellationToken)))
                                 .ToFrozenDictionary(cancellationToken);

    private static string NormalizeDto(ProductGroupDto dto) =>
        nameof(ProductGroupDto);

    public static async ValueTask ValidatePublisherChanges(ProductName productName, ManagementServiceDirectory serviceDirectory, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var fileResources = await GetFileResources(productName, serviceDirectory, cancellationToken);
        await ValidatePublisherChanges(productName, fileResources, serviceUri, pipeline, cancellationToken);
    }

    private static async ValueTask ValidatePublisherChanges(ProductName productName, IDictionary<GroupName, ProductGroupDto> fileResources, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var productmResources = await GetProductmResources(productName, serviceUri, pipeline, cancellationToken);

        var expected = fileResources.MapValue(NormalizeDto);
        var actual = productmResources.MapValue(NormalizeDto);
        actual.Should().BeEquivalentTo(expected);
    }

    public static async ValueTask ValidatePublisherCommitChanges(ProductName productName, CommitId commitId, ManagementServiceDirectory serviceDirectory, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var fileResources = await GetFileResources(productName, commitId, serviceDirectory, cancellationToken);
        await ValidatePublisherChanges(productName, fileResources, serviceUri, pipeline, cancellationToken);
    }

    private static async ValueTask<FrozenDictionary<GroupName, ProductGroupDto>> GetFileResources(ProductName productName, CommitId commitId, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await Git.GetExistingFilesInCommit(serviceDirectory.ToDirectoryInfo(), commitId)
                 .ToAsyncEnumerable()
                 .Choose(file => ProductGroupInformationFile.TryParse(file, serviceDirectory))
                 .Where(file => file.Parent.Parent.Parent.Name == productName)
                 .Choose(async file => await TryGetCommitResource(commitId, serviceDirectory, file, cancellationToken))
                 .ToFrozenDictionary(cancellationToken);

    private static async ValueTask<Option<(GroupName name, ProductGroupDto dto)>> TryGetCommitResource(CommitId commitId, ManagementServiceDirectory serviceDirectory, ProductGroupInformationFile file, CancellationToken cancellationToken)
    {
        var name = file.Parent.Name;
        var contentsOption = Git.TryGetFileContentsInCommit(serviceDirectory.ToDirectoryInfo(), file.ToFileInfo(), commitId);

        return await contentsOption.MapTask(async contents =>
        {
            using (contents)
            {
                var data = await BinaryData.FromStreamAsync(contents, cancellationToken);
                var dto = data.ToObjectFromJson<ProductGroupDto>();
                return (name, dto);
            }
        });
    }
}
