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

internal static class ApiTagModule
{
    public static async ValueTask Put(IEnumerable<ApiTagModel> models, ApiName apiName, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await models.IterParallel(async model =>
        {
            await Put(model, apiName, serviceUri, pipeline, cancellationToken);
        }, cancellationToken);

    private static async ValueTask Put(ApiTagModel model, ApiName apiName, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var uri = ApiTagUri.From(model.Name, apiName, serviceUri);

        await uri.PutDto(ApiTagDto.Instance, pipeline, cancellationToken);
    }

    public static async ValueTask WriteArtifacts(IEnumerable<ApiTagModel> models, ApiName apiName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await models.IterParallel(async model =>
        {
            await WriteInformationFile(model, apiName, serviceDirectory, cancellationToken);
        }, cancellationToken);

    private static async ValueTask WriteInformationFile(ApiTagModel model, ApiName apiName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        var informationFile = ApiTagInformationFile.From(model.Name, apiName, serviceDirectory);
        await informationFile.WriteDto(ApiTagDto.Instance, cancellationToken);
    }

    public static async ValueTask ValidateExtractedArtifacts(ManagementServiceDirectory serviceDirectory, ApiName apiName, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var apimResources = await GetApimResources(apiName, serviceUri, pipeline, cancellationToken);
        var fileResources = await GetFileResources(apiName, serviceDirectory, cancellationToken);

        var expected = apimResources.MapValue(NormalizeDto);
        var actual = fileResources.MapValue(NormalizeDto);

        actual.Should().BeEquivalentTo(expected);
    }

    private static async ValueTask<FrozenDictionary<TagName, ApiTagDto>> GetApimResources(ApiName apiName, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var uri = ApiTagsUri.From(apiName, serviceUri);

        return await uri.List(pipeline, cancellationToken)
                        .ToFrozenDictionary(cancellationToken);
    }

    private static async ValueTask<FrozenDictionary<TagName, ApiTagDto>> GetFileResources(ApiName apiName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await common.ApiTagModule.ListInformationFiles(apiName, serviceDirectory)
                                 .ToAsyncEnumerable()
                                 .SelectAwait(async file => (file.Parent.Name,
                                                             await file.ReadDto(cancellationToken)))
                                 .ToFrozenDictionary(cancellationToken);

    private static string NormalizeDto(ApiTagDto dto) =>
        nameof(ApiTagDto);

    public static async ValueTask ValidatePublisherChanges(ApiName apiName, ManagementServiceDirectory serviceDirectory, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var fileResources = await GetFileResources(apiName, serviceDirectory, cancellationToken);
        await ValidatePublisherChanges(apiName, fileResources, serviceUri, pipeline, cancellationToken);
    }

    private static async ValueTask ValidatePublisherChanges(ApiName apiName, IDictionary<TagName, ApiTagDto> fileResources, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
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

    private static async ValueTask<FrozenDictionary<TagName, ApiTagDto>> GetFileResources(ApiName apiName, CommitId commitId, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await Git.GetExistingFilesInCommit(serviceDirectory.ToDirectoryInfo(), commitId)
                 .ToAsyncEnumerable()
                 .Choose(file => ApiTagInformationFile.TryParse(file, serviceDirectory))
                 .Where(file => file.Parent.Parent.Parent.Name == apiName)
                 .Choose(async file => await TryGetCommitResource(commitId, serviceDirectory, file, cancellationToken))
                 .ToFrozenDictionary(cancellationToken);

    private static async ValueTask<Option<(TagName name, ApiTagDto dto)>> TryGetCommitResource(CommitId commitId, ManagementServiceDirectory serviceDirectory, ApiTagInformationFile file, CancellationToken cancellationToken)
    {
        var name = file.Parent.Name;
        var contentsOption = Git.TryGetFileContentsInCommit(serviceDirectory.ToDirectoryInfo(), file.ToFileInfo(), commitId);

        return await contentsOption.MapTask(async contents =>
        {
            using (contents)
            {
                var data = await BinaryData.FromStreamAsync(contents, cancellationToken);
                var dto = data.ToObjectFromJson<ApiTagDto>();
                return (name, dto);
            }
        });
    }
}
