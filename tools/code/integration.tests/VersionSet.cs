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

public delegate ValueTask DeleteAllVersionSets(ManagementServiceName serviceName, CancellationToken cancellationToken);
public delegate ValueTask PutVersionSetModels(IEnumerable<VersionSetModel> models, ManagementServiceName serviceName, CancellationToken cancellationToken);
public delegate ValueTask ValidateExtractedVersionSets(Option<FrozenSet<VersionSetName>> versionsetNamesOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);
public delegate ValueTask<FrozenDictionary<VersionSetName, VersionSetDto>> GetApimVersionSets(ManagementServiceName serviceName, CancellationToken cancellationToken);
public delegate ValueTask<FrozenDictionary<VersionSetName, VersionSetDto>> GetFileVersionSets(ManagementServiceDirectory serviceDirectory, Option<CommitId> commitIdOption, CancellationToken cancellationToken);
public delegate ValueTask WriteVersionSetModels(IEnumerable<VersionSetModel> models, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);
public delegate ValueTask ValidatePublishedVersionSets(IDictionary<VersionSetName, VersionSetDto> overrides, Option<CommitId> commitIdOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

public static class VersionSetModule
{
    public static void ConfigureDeleteAllVersionSets(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureGetManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteAllVersionSets);
    }

    private static DeleteAllVersionSets GetDeleteAllVersionSets(IServiceProvider provider)
    {
        var getServiceUri = provider.GetRequiredService<GetManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (serviceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteAllVersionSets));

            logger.LogInformation("Deleting all version sets in {ServiceName}...", serviceName);

            var serviceUri = getServiceUri(serviceName);

            await VersionSetsUri.From(serviceUri)
                                .DeleteAll(pipeline, cancellationToken);
        };
    }

    public static void ConfigurePutVersionSetModels(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureGetManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutVersionSetModels);
    }

    private static PutVersionSetModels GetPutVersionSetModels(IServiceProvider provider)
    {
        var getServiceUri = provider.GetRequiredService<GetManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (models, serviceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutVersionSetModels));

            logger.LogInformation("Putting version set models in {ServiceName}...", serviceName);

            await models.IterParallel(async model =>
            {
                await put(model, serviceName, cancellationToken);
            }, cancellationToken);
        };

        async ValueTask put(VersionSetModel model, ManagementServiceName serviceName, CancellationToken cancellationToken)
        {
            var serviceUri = getServiceUri(serviceName);

            var dto = getDto(model);

            await VersionSetUri.From(model.Name, serviceUri)
                               .PutDto(dto, pipeline, cancellationToken);
        }

        static VersionSetDto getDto(VersionSetModel model) =>
            new()
            {
                Properties = new VersionSetDto.VersionSetContract
                {
                    DisplayName = model.DisplayName,
                    Description = model.Description.ValueUnsafe(),
                    VersionHeaderName = model.Scheme is VersioningScheme.Header header ? header.HeaderName : null,
                    VersionQueryName = model.Scheme is VersioningScheme.Query query ? query.QueryName : null,
                    VersioningScheme = model.Scheme switch
                    {
                        VersioningScheme.Header => "Header",
                        VersioningScheme.Query => "Query",
                        VersioningScheme.Segment => "Segment",
                        _ => null
                    }
                }
            };
    }

    public static void ConfigureValidateExtractedVersionSets(IHostApplicationBuilder builder)
    {
        ConfigureGetApimVersionSets(builder);
        ConfigureGetFileVersionSets(builder);

        builder.Services.TryAddSingleton(GetValidateExtractedVersionSets);
    }

    private static ValidateExtractedVersionSets GetValidateExtractedVersionSets(IServiceProvider provider)
    {
        var getApimResources = provider.GetRequiredService<GetApimVersionSets>();
        var tryGetApimGraphQlSchema = provider.GetRequiredService<TryGetApimGraphQlSchema>();
        var getFileResources = provider.GetRequiredService<GetFileVersionSets>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (namesFilterOption, serviceName, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ValidateExtractedVersionSets));

            logger.LogInformation("Validating extracted version sets in {ServiceName}...", serviceName);

            var apimResources = await getApimResources(serviceName, cancellationToken);
            var fileResources = await getFileResources(serviceDirectory, Prelude.None, cancellationToken);

            var expected = apimResources.WhereKey(name => ExtractorOptions.ShouldExtract(name, namesFilterOption))
                                        .MapValue(normalizeDto)
                                        .ToFrozenDictionary();

            var actual = fileResources.MapValue(normalizeDto)
                                      .ToFrozenDictionary();

            actual.Should().BeEquivalentTo(expected);
        };

        static string normalizeDto(VersionSetDto dto) =>
            new
            {
                DisplayName = dto.Properties.DisplayName ?? string.Empty,
                Description = dto.Properties.Description ?? string.Empty,
                VersionHeaderName = dto.Properties.VersionHeaderName ?? string.Empty,
                VersionQueryName = dto.Properties.VersionQueryName ?? string.Empty,
                VersioningScheme = dto.Properties.VersioningScheme ?? string.Empty
            }.ToString()!;
    }

    public static void ConfigureGetApimVersionSets(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureGetManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetGetApimVersionSets);
    }

    private static GetApimVersionSets GetGetApimVersionSets(IServiceProvider provider)
    {
        var getServiceUri = provider.GetRequiredService<GetManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (serviceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(GetApimVersionSets));

            logger.LogInformation("Getting version sets from {ServiceName}...", serviceName);

            var serviceUri = getServiceUri(serviceName);

            return await VersionSetsUri.From(serviceUri)
                                       .List(pipeline, cancellationToken)
                                       .ToFrozenDictionary(cancellationToken);
        };
    }

    public static void ConfigureGetFileVersionSets(IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(GetGetFileVersionSets);
    }

    private static GetFileVersionSets GetGetFileVersionSets(IServiceProvider provider)
    {
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (serviceDirectory, commitIdOption, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(GetFileVersionSets));

            return await commitIdOption.Map(commitId => getWithCommit(serviceDirectory, commitId, cancellationToken))
                                       .IfNone(() => getWithoutCommit(serviceDirectory, cancellationToken));
        };

        async ValueTask<FrozenDictionary<VersionSetName, VersionSetDto>> getWithCommit(ManagementServiceDirectory serviceDirectory, CommitId commitId, CancellationToken cancellationToken)
        {
            using var _ = activitySource.StartActivity(nameof(GetFileVersionSets));

            logger.LogInformation("Getting version sets from {ServiceDirectory} as of commit {CommitId}...", serviceDirectory, commitId);

            return await Git.GetExistingFilesInCommit(serviceDirectory.ToDirectoryInfo(), commitId)
                            .ToAsyncEnumerable()
                            .Choose(file => VersionSetInformationFile.TryParse(file, serviceDirectory))
                            .Choose(async file => await tryGetCommitResource(commitId, serviceDirectory, file, cancellationToken))
                            .ToFrozenDictionary(cancellationToken);
        }

        static async ValueTask<Option<(VersionSetName name, VersionSetDto dto)>> tryGetCommitResource(CommitId commitId, ManagementServiceDirectory serviceDirectory, VersionSetInformationFile file, CancellationToken cancellationToken)
        {
            var name = file.Parent.Name;
            var contentsOption = Git.TryGetFileContentsInCommit(serviceDirectory.ToDirectoryInfo(), file.ToFileInfo(), commitId);

            return await contentsOption.MapTask(async contents =>
            {
                using (contents)
                {
                    var data = await BinaryData.FromStreamAsync(contents, cancellationToken);
                    var dto = data.ToObjectFromJson<VersionSetDto>();
                    return (name, dto);
                }
            });
        }

        async ValueTask<FrozenDictionary<VersionSetName, VersionSetDto>> getWithoutCommit(ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
        {
            logger.LogInformation("Getting version sets from {ServiceDirectory}...", serviceDirectory);

            return await common.VersionSetModule.ListInformationFiles(serviceDirectory)
                                                .ToAsyncEnumerable()
                                                .SelectAwait(async file => (file.Parent.Name,
                                                                            await file.ReadDto(cancellationToken)))
                                                .ToFrozenDictionary(cancellationToken);
        }
    }

    public static void ConfigureWriteVersionSetModels(IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(GetWriteVersionSetModels);
    }

    private static WriteVersionSetModels GetWriteVersionSetModels(IServiceProvider provider)
    {
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (models, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(WriteVersionSetModels));

            logger.LogInformation("Writing version set models to {ServiceDirectory}...", serviceDirectory);

            await models.IterParallel(async model =>
            {
                await writeInformationFile(model, serviceDirectory, cancellationToken);
            }, cancellationToken);
        };

        static async ValueTask writeInformationFile(VersionSetModel model, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
        {
            var informationFile = VersionSetInformationFile.From(model.Name, serviceDirectory);
            var dto = getDto(model);

            await informationFile.WriteDto(dto, cancellationToken);
        }

        static VersionSetDto getDto(VersionSetModel model) =>
            new()
            {
                Properties = new VersionSetDto.VersionSetContract
                {
                    DisplayName = model.DisplayName,
                    Description = model.Description.ValueUnsafe(),
                    VersionHeaderName = model.Scheme is VersioningScheme.Header header ? header.HeaderName : null,
                    VersionQueryName = model.Scheme is VersioningScheme.Query query ? query.QueryName : null,
                    VersioningScheme = model.Scheme switch
                    {
                        VersioningScheme.Header => "Header",
                        VersioningScheme.Query => "Query",
                        VersioningScheme.Segment => "Segment",
                        _ => null
                    }
                }
            };
    }

    public static void ConfigureValidatePublishedVersionSets(IHostApplicationBuilder builder)
    {
        ConfigureGetFileVersionSets(builder);
        ConfigureGetApimVersionSets(builder);

        builder.Services.TryAddSingleton(GetValidatePublishedVersionSets);
    }

    private static ValidatePublishedVersionSets GetValidatePublishedVersionSets(IServiceProvider provider)
    {
        var getFileResources = provider.GetRequiredService<GetFileVersionSets>();
        var getApimResources = provider.GetRequiredService<GetApimVersionSets>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (overrides, commitIdOption, serviceName, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ValidatePublishedVersionSets));

            logger.LogInformation("Validating published version sets in {ServiceDirectory}...", serviceDirectory);

            var apimResources = await getApimResources(serviceName, cancellationToken);
            var fileResources = await getFileResources(serviceDirectory, commitIdOption, cancellationToken);

            var expected = PublisherOptions.Override(fileResources, overrides)
                                           .MapValue(normalizeDto)
                                           .ToFrozenDictionary();

            var actual = apimResources.MapValue(normalizeDto)
                                      .ToFrozenDictionary();

            actual.Should().BeEquivalentTo(expected);
        };

        static string normalizeDto(VersionSetDto dto) =>
            new
            {
                DisplayName = dto.Properties.DisplayName ?? string.Empty,
                Description = dto.Properties.Description ?? string.Empty,
                VersionHeaderName = dto.Properties.VersionHeaderName ?? string.Empty,
                VersionQueryName = dto.Properties.VersionQueryName ?? string.Empty,
                VersioningScheme = dto.Properties.VersioningScheme ?? string.Empty
            }.ToString()!;
    }

    public static Gen<VersionSetModel> GenerateUpdate(VersionSetModel original) =>
        from displayName in VersionSetModel.GenerateDisplayName()
        from scheme in VersioningScheme.Generate()
        from description in VersionSetModel.GenerateDescription().OptionOf()
        select original with
        {
            DisplayName = displayName,
            Scheme = scheme,
            Description = description
        };

    public static Gen<VersionSetDto> GenerateOverride(VersionSetDto original) =>
        from displayName in VersionSetModel.GenerateDisplayName()
        from header in GenerateHeaderOverride(original)
        from query in GenerateQueryOverride(original)
        from description in VersionSetModel.GenerateDescription().OptionOf()
        select new VersionSetDto
        {
            Properties = new VersionSetDto.VersionSetContract
            {
                DisplayName = displayName,
                Description = description.ValueUnsafe(),
                VersionHeaderName = header,
                VersionQueryName = query
            }
        };

    private static Gen<string?> GenerateHeaderOverride(VersionSetDto original) =>
        Gen.OneOf(Gen.Const(original.Properties.VersionHeaderName),
                  string.IsNullOrWhiteSpace(original.Properties.VersionHeaderName)
                  ? Gen.Const(() => null as string)!
                  : VersioningScheme.Header.GenerateHeaderName());

    private static Gen<string?> GenerateQueryOverride(VersionSetDto original) =>
        Gen.OneOf(Gen.Const(original.Properties.VersionQueryName),
                  string.IsNullOrWhiteSpace(original.Properties.VersionQueryName)
                  ? Gen.Const(() => null as string)!
                  : VersioningScheme.Query.GenerateQueryName());

    public static FrozenDictionary<VersionSetName, VersionSetDto> GetDtoDictionary(IEnumerable<VersionSetModel> models) =>
        models.ToFrozenDictionary(model => model.Name, GetDto);

    private static VersionSetDto GetDto(VersionSetModel model) =>
        new()
        {
            Properties = new VersionSetDto.VersionSetContract
            {
                DisplayName = model.DisplayName,
                Description = model.Description.ValueUnsafe(),
                VersionHeaderName = model.Scheme is VersioningScheme.Header header ? header.HeaderName : null,
                VersionQueryName = model.Scheme is VersioningScheme.Query query ? query.QueryName : null,
                VersioningScheme = model.Scheme switch
                {
                    VersioningScheme.Header => "Header",
                    VersioningScheme.Query => "Query",
                    VersioningScheme.Segment => "Segment",
                    _ => null
                }
            }
        };
}