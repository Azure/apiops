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
using static Google.Protobuf.Reflection.SourceCodeInfo.Types;

namespace integration.tests;

public delegate ValueTask DeleteAllGateways(ManagementServiceName serviceName, CancellationToken cancellationToken);
public delegate ValueTask PutGatewayModels(IEnumerable<GatewayModel> models, ManagementServiceName serviceName, CancellationToken cancellationToken);
public delegate ValueTask ValidateExtractedGateways(Option<FrozenSet<GatewayName>> gatewayNamesOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);
public delegate ValueTask<FrozenDictionary<GatewayName, GatewayDto>> GetApimGateways(ManagementServiceName serviceName, CancellationToken cancellationToken);
public delegate ValueTask<FrozenDictionary<GatewayName, GatewayDto>> GetFileGateways(ManagementServiceDirectory serviceDirectory, Option<CommitId> commitIdOption, CancellationToken cancellationToken);
public delegate ValueTask WriteGatewayModels(IEnumerable<GatewayModel> models, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);
public delegate ValueTask ValidatePublishedGateways(IDictionary<GatewayName, GatewayDto> overrides, Option<CommitId> commitIdOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

public static class GatewayModule
{
    public static void ConfigureDeleteAllGateways(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureGetManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteAllGateways);
    }

    private static DeleteAllGateways GetDeleteAllGateways(IServiceProvider provider)
    {
        var getServiceUri = provider.GetRequiredService<GetManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (serviceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteAllGateways));

            logger.LogInformation("Deleting all gateways in {ServiceName}...", serviceName);

            var serviceUri = getServiceUri(serviceName);

            await GatewaysUri.From(serviceUri)
                             .DeleteAll(pipeline, cancellationToken);
        };
    }

    public static void ConfigurePutGatewayModels(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureGetManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutGatewayModels);
    }

    private static PutGatewayModels GetPutGatewayModels(IServiceProvider provider)
    {
        var getServiceUri = provider.GetRequiredService<GetManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (models, serviceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutGatewayModels));

            logger.LogInformation("Putting gateway models in {ServiceName}...", serviceName);

            await models.IterParallel(async model =>
            {
                await put(model, serviceName, cancellationToken);
            }, cancellationToken);
        };

        async ValueTask put(GatewayModel model, ManagementServiceName serviceName, CancellationToken cancellationToken)
        {
            var serviceUri = getServiceUri(serviceName);

            var dto = getDto(model);

            await GatewayUri.From(model.Name, serviceUri)
                            .PutDto(dto, pipeline, cancellationToken);
        }

        static GatewayDto getDto(GatewayModel model) =>
            new()
            {
                Properties = new GatewayDto.GatewayContract
                {
                    Description = model.Description.ValueUnsafe(),
                    LocationData = model.Location switch
                    {
                        GatewayLocation location => new GatewayDto.ResourceLocationDataContract
                        {
                            City = location.City.ValueUnsafe(),
                            CountryOrRegion = location.CountryOrRegion.ValueUnsafe(),
                            District = location.District.ValueUnsafe(),
                            Name = location.Name,
                        }
                    }
                }
            };
    }

    public static void ConfigureValidateExtractedGateways(IHostApplicationBuilder builder)
    {
        ConfigureGetApimGateways(builder);
        ConfigureGetFileGateways(builder);

        builder.Services.TryAddSingleton(GetValidateExtractedGateways);
    }

    private static ValidateExtractedGateways GetValidateExtractedGateways(IServiceProvider provider)
    {
        var getApimResources = provider.GetRequiredService<GetApimGateways>();
        var tryGetApimGraphQlSchema = provider.GetRequiredService<TryGetApimGraphQlSchema>();
        var getFileResources = provider.GetRequiredService<GetFileGateways>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (namesFilterOption, serviceName, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ValidateExtractedGateways));

            logger.LogInformation("Validating extracted gateways in {ServiceName}...", serviceName);

            var apimResources = await getApimResources(serviceName, cancellationToken);
            var fileResources = await getFileResources(serviceDirectory, Prelude.None, cancellationToken);

            var expected = apimResources.WhereKey(name => ExtractorOptions.ShouldExtract(name, namesFilterOption))
                                        .MapValue(normalizeDto)
                                        .ToFrozenDictionary();

            var actual = fileResources.MapValue(normalizeDto)
                                      .ToFrozenDictionary();

            actual.Should().BeEquivalentTo(expected);
        };

        static string normalizeDto(GatewayDto dto) =>
            new
            {
                Description = dto.Properties.Description ?? string.Empty,
                Location = new
                {
                    City = dto.Properties.LocationData?.City ?? string.Empty,
                    CountryOrRegion = dto.Properties.LocationData?.CountryOrRegion ?? string.Empty,
                    District = dto.Properties.LocationData?.District ?? string.Empty,
                    Name = dto.Properties.LocationData?.Name ?? string.Empty
                }.ToString()
            }.ToString()!;
    }

    public static void ConfigureGetApimGateways(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureGetManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetGetApimGateways);
    }

    private static GetApimGateways GetGetApimGateways(IServiceProvider provider)
    {
        var getServiceUri = provider.GetRequiredService<GetManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (serviceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(GetApimGateways));

            logger.LogInformation("Getting gateways from {ServiceName}...", serviceName);

            var serviceUri = getServiceUri(serviceName);

            return await GatewaysUri.From(serviceUri)
                                    .List(pipeline, cancellationToken)
                                    .ToFrozenDictionary(cancellationToken);
        };
    }

    public static void ConfigureGetFileGateways(IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(GetGetFileGateways);
    }

    private static GetFileGateways GetGetFileGateways(IServiceProvider provider)
    {
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (serviceDirectory, commitIdOption, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(GetFileGateways));

            return await commitIdOption.Map(commitId => getWithCommit(serviceDirectory, commitId, cancellationToken))
                                       .IfNone(() => getWithoutCommit(serviceDirectory, cancellationToken));
        };

        async ValueTask<FrozenDictionary<GatewayName, GatewayDto>> getWithCommit(ManagementServiceDirectory serviceDirectory, CommitId commitId, CancellationToken cancellationToken)
        {
            using var _ = activitySource.StartActivity(nameof(GetFileGateways));

            logger.LogInformation("Getting gateways from {ServiceDirectory} as of commit {CommitId}...", serviceDirectory, commitId);

            return await Git.GetExistingFilesInCommit(serviceDirectory.ToDirectoryInfo(), commitId)
                            .ToAsyncEnumerable()
                            .Choose(file => GatewayInformationFile.TryParse(file, serviceDirectory))
                            .Choose(async file => await tryGetCommitResource(commitId, serviceDirectory, file, cancellationToken))
                            .ToFrozenDictionary(cancellationToken);
        }

        static async ValueTask<Option<(GatewayName name, GatewayDto dto)>> tryGetCommitResource(CommitId commitId, ManagementServiceDirectory serviceDirectory, GatewayInformationFile file, CancellationToken cancellationToken)
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

        async ValueTask<FrozenDictionary<GatewayName, GatewayDto>> getWithoutCommit(ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
        {
            logger.LogInformation("Getting gateways from {ServiceDirectory}...", serviceDirectory);

            return await common.GatewayModule.ListInformationFiles(serviceDirectory)
                                             .ToAsyncEnumerable()
                                             .SelectAwait(async file => (file.Parent.Name,
                                                                         await file.ReadDto(cancellationToken)))
                                             .ToFrozenDictionary(cancellationToken);
        }
    }

    public static void ConfigureWriteGatewayModels(IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(GetWriteGatewayModels);
    }

    private static WriteGatewayModels GetWriteGatewayModels(IServiceProvider provider)
    {
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (models, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(WriteGatewayModels));

            logger.LogInformation("Writing gateway models to {ServiceDirectory}...", serviceDirectory);

            await models.IterParallel(async model =>
            {
                await writeInformationFile(model, serviceDirectory, cancellationToken);
            }, cancellationToken);
        };

        static async ValueTask writeInformationFile(GatewayModel model, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
        {
            var informationFile = GatewayInformationFile.From(model.Name, serviceDirectory);
            var dto = getDto(model);

            await informationFile.WriteDto(dto, cancellationToken);
        }

        static GatewayDto getDto(GatewayModel model) =>
            new()
            {
                Properties = new GatewayDto.GatewayContract
                {
                    Description = model.Description.ValueUnsafe(),
                    LocationData = model.Location switch
                    {
                        GatewayLocation location => new GatewayDto.ResourceLocationDataContract
                        {
                            City = location.City.ValueUnsafe(),
                            CountryOrRegion = location.CountryOrRegion.ValueUnsafe(),
                            District = location.District.ValueUnsafe(),
                            Name = location.Name,
                        }
                    }
                }
            };
    }

    public static void ConfigureValidatePublishedGateways(IHostApplicationBuilder builder)
    {
        ConfigureGetFileGateways(builder);
        ConfigureGetApimGateways(builder);

        builder.Services.TryAddSingleton(GetValidatePublishedGateways);
    }

    private static ValidatePublishedGateways GetValidatePublishedGateways(IServiceProvider provider)
    {
        var getFileResources = provider.GetRequiredService<GetFileGateways>();
        var getApimResources = provider.GetRequiredService<GetApimGateways>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (overrides, commitIdOption, serviceName, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ValidatePublishedGateways));

            logger.LogInformation("Validating published gateways in {ServiceDirectory}...", serviceDirectory);

            var apimResources = await getApimResources(serviceName, cancellationToken);
            var fileResources = await getFileResources(serviceDirectory, commitIdOption, cancellationToken);

            var expected = PublisherOptions.Override(fileResources, overrides)
                                           .MapValue(normalizeDto)
                                           .ToFrozenDictionary();

            var actual = apimResources.MapValue(normalizeDto)
                                      .ToFrozenDictionary();

            actual.Should().BeEquivalentTo(expected);
        };

        static string normalizeDto(GatewayDto dto) =>
            new
            {
                Description = dto.Properties.Description ?? string.Empty,
                Location = new
                {
                    City = dto.Properties.LocationData?.City ?? string.Empty,
                    CountryOrRegion = dto.Properties.LocationData?.CountryOrRegion ?? string.Empty,
                    District = dto.Properties.LocationData?.District ?? string.Empty,
                    Name = dto.Properties.LocationData?.Name ?? string.Empty
                }.ToString()
            }.ToString()!;
    }

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

}