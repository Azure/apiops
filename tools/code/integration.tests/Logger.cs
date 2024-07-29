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
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace integration.tests;

public delegate ValueTask DeleteAllLoggers(ManagementServiceName serviceName, CancellationToken cancellationToken);
public delegate ValueTask PutLoggerModels(IEnumerable<LoggerModel> models, ManagementServiceName serviceName, CancellationToken cancellationToken);
public delegate ValueTask ValidateExtractedLoggers(Option<FrozenSet<LoggerName>> loggerNamesOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);
public delegate ValueTask<FrozenDictionary<LoggerName, LoggerDto>> GetApimLoggers(ManagementServiceName serviceName, CancellationToken cancellationToken);
public delegate ValueTask<FrozenDictionary<LoggerName, LoggerDto>> GetFileLoggers(ManagementServiceDirectory serviceDirectory, Option<CommitId> commitIdOption, CancellationToken cancellationToken);
public delegate ValueTask WriteLoggerModels(IEnumerable<LoggerModel> models, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);
public delegate ValueTask ValidatePublishedLoggers(IDictionary<LoggerName, LoggerDto> overrides, Option<CommitId> commitIdOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

public static class LoggerModule
{
    public static void ConfigureDeleteAllLoggers(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureGetManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteAllLoggers);
    }

    private static DeleteAllLoggers GetDeleteAllLoggers(IServiceProvider provider)
    {
        var getServiceUri = provider.GetRequiredService<GetManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (serviceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteAllLoggers));

            logger.LogInformation("Deleting all loggers in {ServiceName}...", serviceName);

            var serviceUri = getServiceUri(serviceName);

            await LoggersUri.From(serviceUri)
                            .DeleteAll(pipeline, cancellationToken);
        };
    }

    public static void ConfigurePutLoggerModels(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureGetManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutLoggerModels);
    }

    private static PutLoggerModels GetPutLoggerModels(IServiceProvider provider)
    {
        var getServiceUri = provider.GetRequiredService<GetManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (models, serviceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutLoggerModels));

            logger.LogInformation("Putting logger models in {ServiceName}...", serviceName);

            await models.IterParallel(async model =>
            {
                await put(model, serviceName, cancellationToken);
            }, cancellationToken);
        };

        async ValueTask put(LoggerModel model, ManagementServiceName serviceName, CancellationToken cancellationToken)
        {
            var serviceUri = getServiceUri(serviceName);

            var dto = getDto(model);

            await LoggerUri.From(model.Name, serviceUri)
                            .PutDto(dto, pipeline, cancellationToken);
        }

        static LoggerDto getDto(LoggerModel model) =>
            new()
            {
                Properties = new LoggerDto.LoggerContract
                {
                    LoggerType = model.Type switch
                    {
                        LoggerType.ApplicationInsights => "applicationInsights",
                        LoggerType.AzureMonitor => "azureMonitor",
                        LoggerType.EventHub => "azureEventHub",
                        _ => throw new ArgumentException($"Model type '{model.Type}' is not supported.", nameof(model))
                    },
                    Description = model.Description.ValueUnsafe(),
                    IsBuffered = model.IsBuffered,
                    ResourceId = model.Type switch
                    {
                        LoggerType.ApplicationInsights applicationInsights => applicationInsights.ResourceId,
                        LoggerType.EventHub eventHub => eventHub.ResourceId,
                        _ => null
                    },
                    Credentials = model.Type switch
                    {
                        LoggerType.ApplicationInsights applicationInsights => new JsonObject
                        {
                            ["instrumentationKey"] = $"{{{{{applicationInsights.InstrumentationKeyNamedValue}}}}}"
                        },
                        LoggerType.EventHub eventHub => new JsonObject
                        {
                            ["name"] = eventHub.Name,
                            ["connectionString"] = $"{{{{{eventHub.ConnectionStringNamedValue}}}}}"
                        },
                        _ => null
                    }
                }
            };
    }

    public static void ConfigureValidateExtractedLoggers(IHostApplicationBuilder builder)
    {
        ConfigureGetApimLoggers(builder);
        ConfigureGetFileLoggers(builder);

        builder.Services.TryAddSingleton(GetValidateExtractedLoggers);
    }

    private static ValidateExtractedLoggers GetValidateExtractedLoggers(IServiceProvider provider)
    {
        var getApimResources = provider.GetRequiredService<GetApimLoggers>();
        var tryGetApimGraphQlSchema = provider.GetRequiredService<TryGetApimGraphQlSchema>();
        var getFileResources = provider.GetRequiredService<GetFileLoggers>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (namesFilterOption, serviceName, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ValidateExtractedLoggers));

            logger.LogInformation("Validating extracted loggers in {ServiceName}...", serviceName);

            var apimResources = await getApimResources(serviceName, cancellationToken);
            var fileResources = await getFileResources(serviceDirectory, Prelude.None, cancellationToken);

            var expected = apimResources.WhereKey(name => ExtractorOptions.ShouldExtract(name, namesFilterOption))
                                        .MapValue(normalizeDto)
                                        .ToFrozenDictionary();

            var actual = fileResources.MapValue(normalizeDto)
                                      .ToFrozenDictionary();

            actual.Should().BeEquivalentTo(expected);
        };

        static string normalizeDto(LoggerDto dto) =>
            new
            {
                LoggerType = dto.Properties.LoggerType ?? string.Empty,
                Description = dto.Properties.Description ?? string.Empty,
                IsBuffered = dto.Properties.IsBuffered ?? false,
                ResourceId = dto.Properties.ResourceId ?? string.Empty,
                Credentials = new
                {
                    Name = dto.Properties.Credentials?.TryGetStringProperty("name").ValueUnsafe() ?? string.Empty,
                    ConnectionString = dto.Properties.Credentials?.TryGetStringProperty("connectionString").ValueUnsafe() ?? string.Empty,
                    InstrumentationKey = dto.Properties.Credentials?.TryGetStringProperty("instrumentationKey").ValueUnsafe() ?? string.Empty
                }
            }.ToString()!;
    }

    public static void ConfigureGetApimLoggers(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureGetManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetGetApimLoggers);
    }

    private static GetApimLoggers GetGetApimLoggers(IServiceProvider provider)
    {
        var getServiceUri = provider.GetRequiredService<GetManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (serviceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(GetApimLoggers));

            logger.LogInformation("Getting loggers from {ServiceName}...", serviceName);

            var serviceUri = getServiceUri(serviceName);

            return await LoggersUri.From(serviceUri)
                                   .List(pipeline, cancellationToken)
                                   .ToFrozenDictionary(cancellationToken);
        };
    }

    public static void ConfigureGetFileLoggers(IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(GetGetFileLoggers);
    }

    private static GetFileLoggers GetGetFileLoggers(IServiceProvider provider)
    {
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (serviceDirectory, commitIdOption, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(GetFileLoggers));

            return await commitIdOption.Map(commitId => getWithCommit(serviceDirectory, commitId, cancellationToken))
                                       .IfNone(() => getWithoutCommit(serviceDirectory, cancellationToken));
        };

        async ValueTask<FrozenDictionary<LoggerName, LoggerDto>> getWithCommit(ManagementServiceDirectory serviceDirectory, CommitId commitId, CancellationToken cancellationToken)
        {
            using var _ = activitySource.StartActivity(nameof(GetFileLoggers));

            logger.LogInformation("Getting loggers from {ServiceDirectory} as of commit {CommitId}...", serviceDirectory, commitId);

            return await Git.GetExistingFilesInCommit(serviceDirectory.ToDirectoryInfo(), commitId)
                            .ToAsyncEnumerable()
                            .Choose(file => LoggerInformationFile.TryParse(file, serviceDirectory))
                            .Choose(async file => await tryGetCommitResource(commitId, serviceDirectory, file, cancellationToken))
                            .ToFrozenDictionary(cancellationToken);
        }

        static async ValueTask<Option<(LoggerName name, LoggerDto dto)>> tryGetCommitResource(CommitId commitId, ManagementServiceDirectory serviceDirectory, LoggerInformationFile file, CancellationToken cancellationToken)
        {
            var name = file.Parent.Name;
            var contentsOption = Git.TryGetFileContentsInCommit(serviceDirectory.ToDirectoryInfo(), file.ToFileInfo(), commitId);

            return await contentsOption.MapTask(async contents =>
            {
                using (contents)
                {
                    var data = await BinaryData.FromStreamAsync(contents, cancellationToken);
                    var dto = data.ToObjectFromJson<LoggerDto>();
                    return (name, dto);
                }
            });
        }

        async ValueTask<FrozenDictionary<LoggerName, LoggerDto>> getWithoutCommit(ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
        {
            logger.LogInformation("Getting loggers from {ServiceDirectory}...", serviceDirectory);

            return await common.LoggerModule.ListInformationFiles(serviceDirectory)
                                             .ToAsyncEnumerable()
                                             .SelectAwait(async file => (file.Parent.Name,
                                                                         await file.ReadDto(cancellationToken)))
                                             .ToFrozenDictionary(cancellationToken);
        }
    }

    public static void ConfigureWriteLoggerModels(IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(GetWriteLoggerModels);
    }

    private static WriteLoggerModels GetWriteLoggerModels(IServiceProvider provider)
    {
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (models, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(WriteLoggerModels));

            logger.LogInformation("Writing logger models to {ServiceDirectory}...", serviceDirectory);

            await models.IterParallel(async model =>
            {
                await writeInformationFile(model, serviceDirectory, cancellationToken);
            }, cancellationToken);
        };

        static async ValueTask writeInformationFile(LoggerModel model, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
        {
            var informationFile = LoggerInformationFile.From(model.Name, serviceDirectory);
            var dto = getDto(model);

            await informationFile.WriteDto(dto, cancellationToken);
        }

        static LoggerDto getDto(LoggerModel model) =>
            new()
            {
                Properties = new LoggerDto.LoggerContract
                {
                    LoggerType = model.Type switch
                    {
                        LoggerType.ApplicationInsights => "applicationInsights",
                        LoggerType.AzureMonitor => "azureMonitor",
                        LoggerType.EventHub => "azureEventHub",
                        _ => throw new ArgumentException($"Model type '{model.Type}' is not supported.", nameof(model))
                    },
                    Description = model.Description.ValueUnsafe(),
                    IsBuffered = model.IsBuffered,
                    ResourceId = model.Type switch
                    {
                        LoggerType.ApplicationInsights applicationInsights => applicationInsights.ResourceId,
                        LoggerType.EventHub eventHub => eventHub.ResourceId,
                        _ => null
                    },
                    Credentials = model.Type switch
                    {
                        LoggerType.ApplicationInsights applicationInsights => new JsonObject
                        {
                            ["instrumentationKey"] = $"{{{{{applicationInsights.InstrumentationKeyNamedValue}}}}}"
                        },
                        LoggerType.EventHub eventHub => new JsonObject
                        {
                            ["name"] = eventHub.Name,
                            ["connectionString"] = $"{{{{{eventHub.ConnectionStringNamedValue}}}}}"
                        },
                        _ => null
                    }
                }
            };
    }

    public static void ConfigureValidatePublishedLoggers(IHostApplicationBuilder builder)
    {
        ConfigureGetFileLoggers(builder);
        ConfigureGetApimLoggers(builder);

        builder.Services.TryAddSingleton(GetValidatePublishedLoggers);
    }

    private static ValidatePublishedLoggers GetValidatePublishedLoggers(IServiceProvider provider)
    {
        var getFileResources = provider.GetRequiredService<GetFileLoggers>();
        var getApimResources = provider.GetRequiredService<GetApimLoggers>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (overrides, commitIdOption, serviceName, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ValidatePublishedLoggers));

            logger.LogInformation("Validating published loggers in {ServiceDirectory}...", serviceDirectory);

            var apimResources = await getApimResources(serviceName, cancellationToken);
            var fileResources = await getFileResources(serviceDirectory, commitIdOption, cancellationToken);

            var expected = PublisherOptions.Override(fileResources, overrides)
                                           .MapValue(normalizeDto)
                                           .ToFrozenDictionary();

            var actual = apimResources.MapValue(normalizeDto)
                                      .ToFrozenDictionary();

            actual.Should().BeEquivalentTo(expected);
        };

        static string normalizeDto(LoggerDto dto) =>
            new
            {
                LoggerType = dto.Properties.LoggerType ?? string.Empty,
                Description = dto.Properties.Description ?? string.Empty,
                IsBuffered = dto.Properties.IsBuffered ?? false,
                ResourceId = dto.Properties.ResourceId ?? string.Empty,
                Credentials = new
                {
                    Name = dto.Properties.Credentials?.TryGetStringProperty("name").ValueUnsafe() ?? string.Empty,
                    ConnectionString = dto.Properties.Credentials?.TryGetStringProperty("connectionString").ValueUnsafe() ?? string.Empty,
                    InstrumentationKey = dto.Properties.Credentials?.TryGetStringProperty("instrumentationKey").ValueUnsafe() ?? string.Empty
                }
            }.ToString()!;
    }

    public static Gen<LoggerModel> GenerateUpdate(LoggerModel original) =>
        from type in LoggerType.Generate()
        from description in LoggerModel.GenerateDescription().OptionOf()
        from isBuffered in Gen.Bool
        select original with
        {
            Type = type,
            Description = description,
            IsBuffered = isBuffered
        };

    public static Gen<LoggerDto> GenerateOverride(LoggerDto original) =>
        from description in LoggerModel.GenerateDescription().OptionOf()
        from isBuffered in Gen.Bool
        select new LoggerDto
        {
            Properties = new LoggerDto.LoggerContract
            {
                Description = description.ValueUnsafe(),
                IsBuffered = isBuffered
            }
        };

    public static FrozenDictionary<LoggerName, LoggerDto> GetDtoDictionary(IEnumerable<LoggerModel> models) =>
        models.ToFrozenDictionary(model => model.Name, GetDto);

    private static LoggerDto GetDto(LoggerModel model) =>
        new()
        {
            Properties = new LoggerDto.LoggerContract
            {
                LoggerType = model.Type switch
                {
                    LoggerType.ApplicationInsights => "applicationInsights",
                    LoggerType.AzureMonitor => "azureMonitor",
                    LoggerType.EventHub => "azureEventHub",
                    _ => throw new ArgumentException($"Model type '{model.Type}' is not supported.", nameof(model))
                },
                Description = model.Description.ValueUnsafe(),
                IsBuffered = model.IsBuffered,
                ResourceId = model.Type switch
                {
                    LoggerType.ApplicationInsights applicationInsights => applicationInsights.ResourceId,
                    LoggerType.EventHub eventHub => eventHub.ResourceId,
                    _ => null
                },
                Credentials = model.Type switch
                {
                    LoggerType.ApplicationInsights applicationInsights => new JsonObject
                    {
                        ["instrumentationKey"] = $"{{{{{applicationInsights.InstrumentationKeyNamedValue}}}}}"
                    },
                    LoggerType.EventHub eventHub => new JsonObject
                    {
                        ["name"] = eventHub.Name,
                        ["connectionString"] = $"{{{{{eventHub.ConnectionStringNamedValue}}}}}"
                    },
                    _ => null
                }
            }
        };
}