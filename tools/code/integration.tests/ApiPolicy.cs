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

internal static class ApiPolicyModule
{
    public static Gen<ApiPolicyModel> GenerateUpdate(ApiPolicyModel original) =>
        from content in ApiPolicyModel.GenerateContent()
        select original with
        {
            Content = content
        };

    private static ApiPolicyDto GetDto(ApiPolicyModel model) =>
        new()
        {
            Properties = new ApiPolicyDto.ApiPolicyContract
            {
                Format = "rawxml",
                Value = model.Content
            }
        };

    public static async ValueTask Put(IEnumerable<ApiPolicyModel> models, ApiName apiName, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await models.IterParallel(async model =>
        {
            await Put(model, apiName, serviceUri, pipeline, cancellationToken);
        }, cancellationToken);

    private static async ValueTask Put(ApiPolicyModel model, ApiName apiName, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var uri = ApiPolicyUri.From(model.Name, apiName, serviceUri);
        var dto = GetDto(model);

        await uri.PutDto(dto, pipeline, cancellationToken);
    }

    public static async ValueTask WriteArtifacts(IEnumerable<ApiPolicyModel> models, ApiName apiName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await models.IterParallel(async model =>
        {
            await WritePolicyFile(model, apiName, serviceDirectory, cancellationToken);
        }, cancellationToken);

    private static async ValueTask WritePolicyFile(ApiPolicyModel model, ApiName apiName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        var policyFile = ApiPolicyFile.From(model.Name, apiName, serviceDirectory);
        await policyFile.WritePolicy(model.Content, cancellationToken);
    }

    public static async ValueTask ValidateExtractedArtifacts(ManagementServiceDirectory serviceDirectory, ApiName apiName, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var apimResources = await GetApimResources(apiName, serviceUri, pipeline, cancellationToken);
        var fileResources = await GetFileResources(apiName, serviceDirectory, cancellationToken);

        var expected = apimResources.MapValue(NormalizeDto);
        var actual = fileResources.MapValue(NormalizeDto);

        actual.Should().BeEquivalentTo(expected);
    }

    private static async ValueTask<FrozenDictionary<ApiPolicyName, ApiPolicyDto>> GetApimResources(ApiName apiName, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var uri = ApiPoliciesUri.From(apiName, serviceUri);

        return await uri.List(pipeline, cancellationToken)
                        .ToFrozenDictionary(cancellationToken);
    }

    private static async ValueTask<FrozenDictionary<ApiPolicyName, ApiPolicyDto>> GetFileResources(ApiName apiName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await common.ApiPolicyModule.ListPolicyFiles(apiName, serviceDirectory)
                                    .ToAsyncEnumerable()
                                    .SelectAwait(async file => (file.Name,
                                                                new ApiPolicyDto
                                                                {
                                                                    Properties = new ApiPolicyDto.ApiPolicyContract
                                                                    {
                                                                        Value = await file.ReadPolicy(cancellationToken)
                                                                    }
                                                                }))
                                    .ToFrozenDictionary(cancellationToken);

    private static string NormalizeDto(ApiPolicyDto dto) =>
        new
        {
            Value = new string((dto.Properties.Value ?? string.Empty)
                                .ReplaceLineEndings(string.Empty)
                                .Where(c => char.IsWhiteSpace(c) is false)
                                .ToArray())
        }.ToString()!;

    public static async ValueTask ValidatePublisherChanges(ApiName apiName, ManagementServiceDirectory serviceDirectory, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var fileResources = await GetFileResources(apiName, serviceDirectory, cancellationToken);
        await ValidatePublisherChanges(apiName, fileResources, serviceUri, pipeline, cancellationToken);
    }

    private static async ValueTask ValidatePublisherChanges(ApiName apiName, IDictionary<ApiPolicyName, ApiPolicyDto> fileResources, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var apimResources = await GetApimResources(apiName, serviceUri, pipeline, cancellationToken);

        var expected = fileResources.MapValue(NormalizeDto);
        var actual = apimResources.MapValue(NormalizeDto);
        actual.Should().BeEquivalentTo(expected);
    }

    public static async ValueTask ValidatePublisherCommitChanges(ApiName apiName, CommitId commitId, ManagementServiceDirectory serviceDirectory, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var fileResources = await GetFileResources(apiName, commitId, serviceDirectory, cancellationToken);
        await ValidatePublisherChanges(apiName, fileResources, serviceUri, pipeline, cancellationToken);
    }

    private static async ValueTask<FrozenDictionary<ApiPolicyName, ApiPolicyDto>> GetFileResources(ApiName apiName, CommitId commitId, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await Git.GetExistingFilesInCommit(serviceDirectory.ToDirectoryInfo(), commitId)
                 .ToAsyncEnumerable()
                 .Choose(file => ApiPolicyFile.TryParse(file, serviceDirectory))
                 .Where(file => file.Parent.Name == apiName)
                 .Choose(async file => await TryGetCommitResource(commitId, serviceDirectory, file, cancellationToken))
                 .ToFrozenDictionary(cancellationToken);

    private static async ValueTask<Option<(ApiPolicyName name, ApiPolicyDto dto)>> TryGetCommitResource(CommitId commitId, ManagementServiceDirectory serviceDirectory, ApiPolicyFile file, CancellationToken cancellationToken)
    {
        var name = file.Name;
        var contentsOption = Git.TryGetFileContentsInCommit(serviceDirectory.ToDirectoryInfo(), file.ToFileInfo(), commitId);

        return await contentsOption.MapTask(async contents =>
        {
            using (contents)
            {
                var data = await BinaryData.FromStreamAsync(contents, cancellationToken);
                var dto = new ApiPolicyDto
                {
                    Properties = new ApiPolicyDto.ApiPolicyContract
                    {
                        Value = data.ToString()
                    }
                };
                return (name, dto);
            }
        });
    }
}
