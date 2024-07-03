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

internal delegate ValueTask DeleteAllGateways(ManagementServiceName serviceName, CancellationToken cancellationToken);

file sealed class DeleteAllGatewaysHandler(ILogger<DeleteAllGateways> logger, GetManagementServiceUri getServiceUri, HttpPipeline pipeline, ActivitySource activitySource)
{
    public async ValueTask Handle(ManagementServiceName serviceName, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(DeleteAllGateways));

        logger.LogInformation("Deleting all gateways in {ServiceName}.", serviceName);
        var serviceUri = getServiceUri(serviceName);
        await GatewaysUri.From(serviceUri).DeleteAll(pipeline, cancellationToken);
    }
}

internal static class GatewayServices
{
    public static void ConfigureDeleteAllGateways(IServiceCollection services)
    {
        ManagementServices.ConfigureGetManagementServiceUri(services);

        services.TryAddSingleton<DeleteAllGatewaysHandler>();
        services.TryAddSingleton<DeleteAllGateways>(provider => provider.GetRequiredService<DeleteAllGatewaysHandler>().Handle);
    }
}

internal static class Gateway
{
    public static Gen<GatewayModel> GenerateUpdate(GatewayModel original) =>
        from description in GatewayModel.GenerateDescription().OptionOf()
        from location in GatewayLocation.Generate()
        select original with
        {
            Description = description,
            Location = location,
        };

    public static Gen<GatewayDto> GenerateOverride(GatewayDto original) =>
        from description in GatewayModel.GenerateDescription().OptionOf()
        from location in GatewayLocation.Generate()
        select new GatewayDto
        {
            Properties = new GatewayDto.GatewayContract
            {
                Description = description.ValueUnsafe(),
                LocationData = ModelLocationToDtoContract(location)
            }
        };

    private static GatewayDto.ResourceLocationDataContract ModelLocationToDtoContract(GatewayLocation location) =>
        new()
        {
            City = location.City.ValueUnsafe(),
            CountryOrRegion = location.CountryOrRegion.ValueUnsafe(),
            District = location.District.ValueUnsafe(),
            Name = location.Name
        };

    public static FrozenDictionary<GatewayName, GatewayDto> GetDtoDictionary(IEnumerable<GatewayModel> models) =>
        models.ToFrozenDictionary(model => model.Name, GetDto);

    private static GatewayDto GetDto(GatewayModel model) =>
        new()
        {
            Properties = new GatewayDto.GatewayContract
            {
                Description = model.Description.ValueUnsafe(),
                LocationData = ModelLocationToDtoContract(model.Location)
            }
        };

    public static async ValueTask Put(IEnumerable<GatewayModel> models, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await models.IterParallel(async model =>
        {
            await Put(model, serviceUri, pipeline, cancellationToken);
        }, cancellationToken);

    private static async ValueTask Put(GatewayModel model, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var uri = GatewayUri.From(model.Name, serviceUri);
        var dto = GetDto(model);

        await uri.PutDto(dto, pipeline, cancellationToken);

        await GatewayApi.Put(model.Apis, model.Name, serviceUri, pipeline, cancellationToken);
    }

    public static async ValueTask DeleteAll(ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await GatewaysUri.From(serviceUri).DeleteAll(pipeline, cancellationToken);

    public static async ValueTask WriteArtifacts(IEnumerable<GatewayModel> models, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await models.IterParallel(async model =>
        {
            await WriteInformationFile(model, serviceDirectory, cancellationToken);

            await GatewayApi.WriteArtifacts(model.Apis, model.Name, serviceDirectory, cancellationToken);
        }, cancellationToken);

    private static async ValueTask WriteInformationFile(GatewayModel model, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        var informationFile = GatewayInformationFile.From(model.Name, serviceDirectory);
        var dto = GetDto(model);

        await informationFile.WriteDto(dto, cancellationToken);
    }

    public static async ValueTask ValidateExtractedArtifacts(Option<FrozenSet<GatewayName>> namesToExtract, ManagementServiceDirectory serviceDirectory, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var apimResources = await GetApimResources(serviceUri, pipeline, cancellationToken);
        var fileResources = await GetFileResources(serviceDirectory, cancellationToken);

        var expected = apimResources.WhereKey(name => ExtractorOptions.ShouldExtract(name, namesToExtract))
                                    .MapValue(NormalizeDto);
        var actual = fileResources.MapValue(NormalizeDto);

        actual.Should().BeEquivalentTo(expected);

        await expected.IterParallel(async kvp =>
        {
            var gatewayName = kvp.Key;

            await GatewayApi.ValidateExtractedArtifacts(serviceDirectory, gatewayName, serviceUri, pipeline, cancellationToken);
        }, cancellationToken);
    }

    private static async ValueTask<FrozenDictionary<GatewayName, GatewayDto>> GetApimResources(ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var uri = GatewaysUri.From(serviceUri);

        return await uri.List(pipeline, cancellationToken)
                        .ToFrozenDictionary(cancellationToken);
    }

    private static async ValueTask<FrozenDictionary<GatewayName, GatewayDto>> GetFileResources(ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await GatewayModule.ListInformationFiles(serviceDirectory)
                              .ToAsyncEnumerable()
                              .SelectAwait(async file => (file.Parent.Name,
                                                          await file.ReadDto(cancellationToken)))
                              .ToFrozenDictionary(cancellationToken);

    private static string NormalizeDto(GatewayDto dto) =>
        new
        {
            Description = dto.Properties.Description ?? string.Empty,
            LocationData = dto.Properties.LocationData is null
                           ? null
                           : new
                           {
                               Name = dto.Properties.LocationData.Name ?? string.Empty,
                               City = dto.Properties.LocationData.City ?? string.Empty,
                               CountryOrRegion = dto.Properties.LocationData.CountryOrRegion ?? string.Empty,
                               District = dto.Properties.LocationData.District ?? string.Empty
                           }
        }.ToString()!;

    public static async ValueTask ValidatePublisherChanges(ManagementServiceDirectory serviceDirectory, IDictionary<GatewayName, GatewayDto> overrides, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var fileResources = await GetFileResources(serviceDirectory, cancellationToken);
        await ValidatePublisherChanges(fileResources, overrides, serviceUri, pipeline, cancellationToken);

        await fileResources.Keys.IterParallel(async gatewayName =>
        {
            await GatewayApi.ValidatePublisherChanges(gatewayName, serviceDirectory, serviceUri, pipeline, cancellationToken);
        }, cancellationToken);
    }

    private static async ValueTask ValidatePublisherChanges(IDictionary<GatewayName, GatewayDto> fileResources, IDictionary<GatewayName, GatewayDto> overrides, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var apimResources = await GetApimResources(serviceUri, pipeline, cancellationToken);

        var expected = PublisherOptions.Override(fileResources, overrides)
                                       .MapValue(NormalizeDto);
        var actual = apimResources.MapValue(NormalizeDto);
        actual.Should().BeEquivalentTo(expected);
    }

    public static async ValueTask ValidatePublisherCommitChanges(CommitId commitId, ManagementServiceDirectory serviceDirectory, IDictionary<GatewayName, GatewayDto> overrides, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var fileResources = await GetFileResources(commitId, serviceDirectory, cancellationToken);
        await ValidatePublisherChanges(fileResources, overrides, serviceUri, pipeline, cancellationToken);

        await fileResources.Keys.IterParallel(async gatewayName =>
        {
            await GatewayApi.ValidatePublisherCommitChanges(gatewayName, commitId, serviceDirectory, serviceUri, pipeline, cancellationToken);
        }, cancellationToken);
    }

    private static async ValueTask<FrozenDictionary<GatewayName, GatewayDto>> GetFileResources(CommitId commitId, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await Git.GetExistingFilesInCommit(serviceDirectory.ToDirectoryInfo(), commitId)
                 .ToAsyncEnumerable()
                 .Choose(file => GatewayInformationFile.TryParse(file, serviceDirectory))
                 .Choose(async file => await TryGetCommitResource(commitId, serviceDirectory, file, cancellationToken))
                 .ToFrozenDictionary(cancellationToken);

    private static async ValueTask<Option<(GatewayName name, GatewayDto dto)>> TryGetCommitResource(CommitId commitId, ManagementServiceDirectory serviceDirectory, GatewayInformationFile file, CancellationToken cancellationToken)
    {
        var name = file.Parent.Name;
        var contentsOption = Git.TryGetFileContentsInCommit(serviceDirectory.ToDirectoryInfo(), file.ToFileInfo(), commitId);

        return await contentsOption.MapTask(async contents =>
        {
            using (contents)
            {
                var data = await BinaryData.FromStreamAsync(contents, cancellationToken);
                var dto = data.ToObjectFromJson<GatewayDto>();
                return (name, dto);
            }
        });
    }
}
