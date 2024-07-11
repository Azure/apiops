using Azure.Core.Pipeline;
using common;
using common.tests;
using CsCheck;
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

internal static class ProductPolicyModule
{
    public static Gen<ProductPolicyModel> GenerateUpdate(ProductPolicyModel original) =>
        from content in ProductPolicyModel.GenerateContent()
        select original with
        {
            Content = content
        };

    private static ProductPolicyDto GetDto(ProductPolicyModel model) =>
        new()
        {
            Properties = new ProductPolicyDto.ProductPolicyContract
            {
                Format = "rawxml",
                Value = model.Content
            }
        };

    public static async ValueTask Put(IEnumerable<ProductPolicyModel> models, ProductName productName, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await models.IterParallel(async model =>
        {
            await Put(model, productName, serviceUri, pipeline, cancellationToken);
        }, cancellationToken);

    private static async ValueTask Put(ProductPolicyModel model, ProductName productName, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var uri = ProductPolicyUri.From(model.Name, productName, serviceUri);
        var dto = GetDto(model);

        await uri.PutDto(dto, pipeline, cancellationToken);
    }

    public static async ValueTask WriteArtifacts(IEnumerable<ProductPolicyModel> models, ProductName productName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await models.IterParallel(async model =>
        {
            await WritePolicyFile(model, productName, serviceDirectory, cancellationToken);
        }, cancellationToken);

    private static async ValueTask WritePolicyFile(ProductPolicyModel model, ProductName productName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        var policyFile = ProductPolicyFile.From(model.Name, productName, serviceDirectory);
        await policyFile.WritePolicy(model.Content, cancellationToken);
    }

    public static async ValueTask ValidateExtractedArtifacts(ManagementServiceDirectory serviceDirectory, ProductName productName, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var productmResources = await GetProductmResources(productName, serviceUri, pipeline, cancellationToken);
        var fileResources = await GetFileResources(productName, serviceDirectory, cancellationToken);

        var expected = productmResources.MapValue(NormalizeDto);
        var actual = fileResources.MapValue(NormalizeDto);

        actual.Should().BeEquivalentTo(expected);
    }

    private static async ValueTask<FrozenDictionary<ProductPolicyName, ProductPolicyDto>> GetProductmResources(ProductName productName, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var uri = ProductPoliciesUri.From(productName, serviceUri);

        return await uri.List(pipeline, cancellationToken)
                        .ToFrozenDictionary(cancellationToken);
    }

    private static async ValueTask<FrozenDictionary<ProductPolicyName, ProductPolicyDto>> GetFileResources(ProductName productName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await common.ProductPolicyModule.ListPolicyFiles(productName, serviceDirectory)
                                    .ToAsyncEnumerable()
                                    .SelectAwait(async file => (file.Name,
                                                                new ProductPolicyDto
                                                                {
                                                                    Properties = new ProductPolicyDto.ProductPolicyContract
                                                                    {
                                                                        Value = await file.ReadPolicy(cancellationToken)
                                                                    }
                                                                }))
                                    .ToFrozenDictionary(cancellationToken);

    private static string NormalizeDto(ProductPolicyDto dto) =>
        new
        {
            Value = new string((dto.Properties.Value ?? string.Empty)
                                .ReplaceLineEndings(string.Empty)
                                .Where(c => char.IsWhiteSpace(c) is false)
                                .ToArray())
        }.ToString()!;

    public static async ValueTask ValidatePublisherChanges(ProductName productName, ManagementServiceDirectory serviceDirectory, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var fileResources = await GetFileResources(productName, serviceDirectory, cancellationToken);
        await ValidatePublisherChanges(productName, fileResources, serviceUri, pipeline, cancellationToken);
    }

    private static async ValueTask ValidatePublisherChanges(ProductName productName, IDictionary<ProductPolicyName, ProductPolicyDto> fileResources, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
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

    private static async ValueTask<FrozenDictionary<ProductPolicyName, ProductPolicyDto>> GetFileResources(ProductName productName, CommitId commitId, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await Git.GetExistingFilesInCommit(serviceDirectory.ToDirectoryInfo(), commitId)
                 .ToAsyncEnumerable()
                 .Choose(file => ProductPolicyFile.TryParse(file, serviceDirectory))
                 .Where(file => file.Parent.Name == productName)
                 .Choose(async file => await TryGetCommitResource(commitId, serviceDirectory, file, cancellationToken))
                 .ToFrozenDictionary(cancellationToken);

    private static async ValueTask<Option<(ProductPolicyName name, ProductPolicyDto dto)>> TryGetCommitResource(CommitId commitId, ManagementServiceDirectory serviceDirectory, ProductPolicyFile file, CancellationToken cancellationToken)
    {
        var name = file.Name;
        var contentsOption = Git.TryGetFileContentsInCommit(serviceDirectory.ToDirectoryInfo(), file.ToFileInfo(), commitId);

        return await contentsOption.MapTask(async contents =>
        {
            using (contents)
            {
                var data = await BinaryData.FromStreamAsync(contents, cancellationToken);
                var dto = new ProductPolicyDto
                {
                    Properties = new ProductPolicyDto.ProductPolicyContract
                    {
                        Value = data.ToString()
                    }
                };
                return (name, dto);
            }
        });
    }
}
