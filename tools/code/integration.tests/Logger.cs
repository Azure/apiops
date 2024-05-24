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
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace integration.tests;

internal delegate ValueTask DeleteAllLoggers(ManagementServiceName serviceName, CancellationToken cancellationToken);

internal delegate ValueTask PutLoggerModels(IEnumerable<LoggerModel> models, ManagementServiceName serviceName, CancellationToken cancellationToken);

internal delegate ValueTask ValidateExtractedLoggers(Option<FrozenSet<LoggerName>> namesFilterOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

file delegate ValueTask<FrozenDictionary<LoggerName, LoggerDto>> GetApimLoggers(ManagementServiceName serviceName, CancellationToken cancellationToken);

file delegate ValueTask<FrozenDictionary<LoggerName, LoggerDto>> GetFileLoggers(ManagementServiceDirectory serviceDirectory, Option<CommitId> commitIdOption, CancellationToken cancellationToken);

internal delegate ValueTask WriteLoggerModels(IEnumerable<LoggerModel> models, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

internal delegate ValueTask ValidatePublishedLoggers(IDictionary<LoggerName, LoggerDto> overrides, Option<CommitId> commitIdOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

file sealed class DeleteAllLoggersHandler(ILogger<DeleteAllLoggers> logger, GetManagementServiceUri getServiceUri, HttpPipeline pipeline, ActivitySource activitySource)
{
    public async ValueTask Handle(ManagementServiceName serviceName, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(DeleteAllLoggers));

        logger.LogInformation("Deleting all loggers in {ServiceName}...", serviceName);
        var serviceUri = getServiceUri(serviceName);
        await LoggersUri.From(serviceUri).DeleteAll(pipeline, cancellationToken);
    }
}

file sealed class PutLoggerModelsHandler(ILogger<PutLoggerModels> logger, GetManagementServiceUri getServiceUri, HttpPipeline pipeline, ActivitySource activitySource)
{
    public async ValueTask Handle(IEnumerable<LoggerModel> models, ManagementServiceName serviceName, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(PutLoggerModels));

        logger.LogInformation("Putting logger models in {ServiceName}...", serviceName);
        await models.IterParallel(async model =>
        {
            await Put(model, serviceName, cancellationToken);
        }, cancellationToken);
    }

    private async ValueTask Put(LoggerModel model, ManagementServiceName serviceName, CancellationToken cancellationToken)
    {
        var serviceUri = getServiceUri(serviceName);
        var uri = LoggerUri.From(model.Name, serviceUri);
        var dto = GetDto(model);

        await uri.PutDto(dto, pipeline, cancellationToken);
    }

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

file sealed class ValidateExtractedLoggersHandler(ILogger<ValidateExtractedLoggers> logger, GetApimLoggers getApimResources, GetFileLoggers getFileResources, ActivitySource activitySource)
{
    public async ValueTask Handle(Option<FrozenSet<LoggerName>> namesFilterOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(ValidateExtractedLoggers));

        logger.LogInformation("Validating extracted loggers in {ServiceName}...", serviceName);
        var apimResources = await getApimResources(serviceName, cancellationToken);
        var fileResources = await getFileResources(serviceDirectory, Prelude.None, cancellationToken);

        var expected = apimResources.WhereKey(name => ExtractorOptions.ShouldExtract(name, namesFilterOption))
                                    .MapValue(NormalizeDto);
        var actual = fileResources.MapValue(NormalizeDto);

        actual.Should().BeEquivalentTo(expected);
    }

    private static string NormalizeDto(LoggerDto dto) =>
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

file sealed class GetApimLoggersHandler(ILogger<GetApimLoggers> logger, GetManagementServiceUri getServiceUri, HttpPipeline pipeline, ActivitySource activitySource)
{
    public async ValueTask<FrozenDictionary<LoggerName, LoggerDto>> Handle(ManagementServiceName serviceName, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(GetApimLoggers));

        logger.LogInformation("Getting loggers from {ServiceName}...", serviceName);

        var serviceUri = getServiceUri(serviceName);
        var uri = LoggersUri.From(serviceUri);

        return await uri.List(pipeline, cancellationToken)
                        .ToFrozenDictionary(cancellationToken);
    }
}

file sealed class GetFileLoggersHandler(ILogger<GetFileLoggers> logger, ActivitySource activitySource)
{
    public async ValueTask<FrozenDictionary<LoggerName, LoggerDto>> Handle(ManagementServiceDirectory serviceDirectory, Option<CommitId> commitIdOption, CancellationToken cancellationToken) =>
        await commitIdOption.Map(commitId => GetWithCommit(serviceDirectory, commitId, cancellationToken))
                           .IfNone(() => GetWithoutCommit(serviceDirectory, cancellationToken));

    private async ValueTask<FrozenDictionary<LoggerName, LoggerDto>> GetWithCommit(ManagementServiceDirectory serviceDirectory, CommitId commitId, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(GetFileLoggers));

        logger.LogInformation("Getting loggers from {ServiceDirectory} as of commit {CommitId}...", serviceDirectory, commitId);

        return await Git.GetExistingFilesInCommit(serviceDirectory.ToDirectoryInfo(), commitId)
                        .ToAsyncEnumerable()
                        .Choose(file => LoggerInformationFile.TryParse(file, serviceDirectory))
                        .Choose(async file => await TryGetCommitResource(commitId, serviceDirectory, file, cancellationToken))
                        .ToFrozenDictionary(cancellationToken);
    }

    private static async ValueTask<Option<(LoggerName name, LoggerDto dto)>> TryGetCommitResource(CommitId commitId, ManagementServiceDirectory serviceDirectory, LoggerInformationFile file, CancellationToken cancellationToken)
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

    private async ValueTask<FrozenDictionary<LoggerName, LoggerDto>> GetWithoutCommit(ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(GetFileLoggers));

        logger.LogInformation("Getting loggers from {ServiceDirectory}...", serviceDirectory);

        return await LoggerModule.ListInformationFiles(serviceDirectory)
                              .ToAsyncEnumerable()
                              .SelectAwait(async file => (file.Parent.Name,
                                                          await file.ReadDto(cancellationToken)))
                              .ToFrozenDictionary(cancellationToken);
    }
}

file sealed class WriteLoggerModelsHandler(ILogger<WriteLoggerModels> logger, ActivitySource activitySource)
{
    public async ValueTask Handle(IEnumerable<LoggerModel> models, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(WriteLoggerModels));

        logger.LogInformation("Writing logger models to {ServiceDirectory}...", serviceDirectory);
        await models.IterParallel(async model =>
        {
            await WriteInformationFile(model, serviceDirectory, cancellationToken);
        }, cancellationToken);
    }

    private static async ValueTask WriteInformationFile(LoggerModel model, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        var informationFile = LoggerInformationFile.From(model.Name, serviceDirectory);
        var dto = GetDto(model);

        await informationFile.WriteDto(dto, cancellationToken);
    }

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

file sealed class ValidatePublishedLoggersHandler(ILogger<ValidatePublishedLoggers> logger, GetFileLoggers getFileResources, GetApimLoggers getApimResources, ActivitySource activitySource)
{
    public async ValueTask Handle(IDictionary<LoggerName, LoggerDto> overrides, Option<CommitId> commitIdOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(ValidatePublishedLoggers));

        logger.LogInformation("Validating published loggers in {ServiceDirectory}...", serviceDirectory);

        var apimResources = await getApimResources(serviceName, cancellationToken);
        var fileResources = await getFileResources(serviceDirectory, commitIdOption, cancellationToken);

        var expected = PublisherOptions.Override(fileResources, overrides)
                                       .MapValue(NormalizeDto);
        var actual = apimResources.MapValue(NormalizeDto);

        actual.Should().BeEquivalentTo(expected);
    }

    private static string NormalizeDto(LoggerDto dto) =>
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

internal static class LoggerServices
{
    public static void ConfigureDeleteAllLoggers(IServiceCollection services)
    {
        ManagementServices.ConfigureGetManagementServiceUri(services);

        services.TryAddSingleton<DeleteAllLoggersHandler>();
        services.TryAddSingleton<DeleteAllLoggers>(provider => provider.GetRequiredService<DeleteAllLoggersHandler>().Handle);
    }

    public static void ConfigurePutLoggerModels(IServiceCollection services)
    {
        ManagementServices.ConfigureGetManagementServiceUri(services);

        services.TryAddSingleton<PutLoggerModelsHandler>();
        services.TryAddSingleton<PutLoggerModels>(provider => provider.GetRequiredService<PutLoggerModelsHandler>().Handle);
    }

    public static void ConfigureValidateExtractedLoggers(IServiceCollection services)
    {
        ConfigureGetApimLoggers(services);
        ConfigureGetFileLoggers(services);

        services.TryAddSingleton<ValidateExtractedLoggersHandler>();
        services.TryAddSingleton<ValidateExtractedLoggers>(provider => provider.GetRequiredService<ValidateExtractedLoggersHandler>().Handle);
    }

    private static void ConfigureGetApimLoggers(IServiceCollection services)
    {
        ManagementServices.ConfigureGetManagementServiceUri(services);

        services.TryAddSingleton<GetApimLoggersHandler>();
        services.TryAddSingleton<GetApimLoggers>(provider => provider.GetRequiredService<GetApimLoggersHandler>().Handle);
    }

    private static void ConfigureGetFileLoggers(IServiceCollection services)
    {
        services.TryAddSingleton<GetFileLoggersHandler>();
        services.TryAddSingleton<GetFileLoggers>(provider => provider.GetRequiredService<GetFileLoggersHandler>().Handle);
    }

    public static void ConfigureWriteLoggerModels(IServiceCollection services)
    {
        services.TryAddSingleton<WriteLoggerModelsHandler>();
        services.TryAddSingleton<WriteLoggerModels>(provider => provider.GetRequiredService<WriteLoggerModelsHandler>().Handle);
    }

    public static void ConfigureValidatePublishedLoggers(IServiceCollection services)
    {
        ConfigureGetFileLoggers(services);
        ConfigureGetApimLoggers(services);

        services.TryAddSingleton<ValidatePublishedLoggersHandler>();
        services.TryAddSingleton<ValidatePublishedLoggers>(provider => provider.GetRequiredService<ValidatePublishedLoggersHandler>().Handle);
    }
}

internal static class Logger
{
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
