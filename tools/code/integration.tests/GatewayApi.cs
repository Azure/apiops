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

internal static class GatewayApiModule
{
    public static async ValueTask Put(IEnumerable<GatewayApiModel> models, GatewayName gatewayName, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await models.IterParallel(async model =>
        {
            await Put(model, gatewayName, serviceUri, pipeline, cancellationToken);
        }, cancellationToken);

    private static async ValueTask Put(GatewayApiModel model, GatewayName gatewayName, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var uri = GatewayApiUri.From(model.Name, gatewayName, serviceUri);

        await uri.PutDto(GatewayApiDto.Instance, pipeline, cancellationToken);
    }

    public static async ValueTask WriteArtifacts(IEnumerable<GatewayApiModel> models, GatewayName gatewayName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await models.IterParallel(async model =>
        {
            await WriteInformationFile(model, gatewayName, serviceDirectory, cancellationToken);
        }, cancellationToken);

    private static async ValueTask WriteInformationFile(GatewayApiModel model, GatewayName gatewayName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        var informationFile = GatewayApiInformationFile.From(model.Name, gatewayName, serviceDirectory);
        await informationFile.WriteDto(GatewayApiDto.Instance, cancellationToken);
    }

    public static async ValueTask ValidateExtractedArtifacts(ManagementServiceDirectory serviceDirectory, GatewayName gatewayName, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var gatewaymResources = await GetGatewaymResources(gatewayName, serviceUri, pipeline, cancellationToken);
        var fileResources = await GetFileResources(gatewayName, serviceDirectory, cancellationToken);

        var expected = gatewaymResources.MapValue(NormalizeDto);
        var actual = fileResources.MapValue(NormalizeDto);

        actual.Should().BeEquivalentTo(expected);
    }

    private static async ValueTask<FrozenDictionary<ApiName, GatewayApiDto>> GetGatewaymResources(GatewayName gatewayName, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var uri = GatewayApisUri.From(gatewayName, serviceUri);

        return await uri.List(pipeline, cancellationToken)
                        .ToFrozenDictionary(cancellationToken);
    }

    private static async ValueTask<FrozenDictionary<ApiName, GatewayApiDto>> GetFileResources(GatewayName gatewayName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await common.GatewayApiModule.ListInformationFiles(gatewayName, serviceDirectory)
                                 .ToAsyncEnumerable()
                                 .SelectAwait(async file => (file.Parent.Name,
                                                             await file.ReadDto(cancellationToken)))
                                 .ToFrozenDictionary(cancellationToken);

    private static string NormalizeDto(GatewayApiDto dto) =>
        nameof(GatewayApiDto);

    public static async ValueTask ValidatePublisherChanges(GatewayName gatewayName, ManagementServiceDirectory serviceDirectory, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var fileResources = await GetFileResources(gatewayName, serviceDirectory, cancellationToken);
        await ValidatePublisherChanges(gatewayName, fileResources, serviceUri, pipeline, cancellationToken);
    }

    private static async ValueTask ValidatePublisherChanges(GatewayName gatewayName, IDictionary<ApiName, GatewayApiDto> fileResources, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var gatewaymResources = await GetGatewaymResources(gatewayName, serviceUri, pipeline, cancellationToken);

        var expected = fileResources.MapValue(NormalizeDto);
        var actual = gatewaymResources.MapValue(NormalizeDto);
        actual.Should().BeEquivalentTo(expected);
    }

    public static async ValueTask ValidatePublisherCommitChanges(GatewayName gatewayName, CommitId commitId, ManagementServiceDirectory serviceDirectory, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var fileResources = await GetFileResources(gatewayName, commitId, serviceDirectory, cancellationToken);
        await ValidatePublisherChanges(gatewayName, fileResources, serviceUri, pipeline, cancellationToken);
    }

    private static async ValueTask<FrozenDictionary<ApiName, GatewayApiDto>> GetFileResources(GatewayName gatewayName, CommitId commitId, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await Git.GetExistingFilesInCommit(serviceDirectory.ToDirectoryInfo(), commitId)
                 .ToAsyncEnumerable()
                 .Choose(file => GatewayApiInformationFile.TryParse(file, serviceDirectory))
                 .Where(file => file.Parent.Parent.Parent.Name == gatewayName)
                 .Choose(async file => await TryGetCommitResource(commitId, serviceDirectory, file, cancellationToken))
                 .ToFrozenDictionary(cancellationToken);

    private static async ValueTask<Option<(ApiName name, GatewayApiDto dto)>> TryGetCommitResource(CommitId commitId, ManagementServiceDirectory serviceDirectory, GatewayApiInformationFile file, CancellationToken cancellationToken)
    {
        var name = file.Parent.Name;
        var contentsOption = Git.TryGetFileContentsInCommit(serviceDirectory.ToDirectoryInfo(), file.ToFileInfo(), commitId);

        return await contentsOption.MapTask(async contents =>
        {
            using (contents)
            {
                var data = await BinaryData.FromStreamAsync(contents, cancellationToken);
                var dto = data.ToObjectFromJson<GatewayApiDto>();
                return (name, dto);
            }
        });
    }
}
