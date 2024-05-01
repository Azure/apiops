using Azure.Core.Pipeline;
using common;
using common.tests;
using CsCheck;
using FluentAssertions;
using LanguageExt;
using LanguageExt.UnsafeValueAccess;
using publisher;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace integration.tests;

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

    public static async ValueTask Put(IEnumerable<LoggerModel> models, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await models.IterParallel(async model =>
        {
            await Put(model, serviceUri, pipeline, cancellationToken);
        }, cancellationToken);

    private static async ValueTask Put(LoggerModel model, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var uri = LoggerUri.From(model.Name, serviceUri);
        var dto = GetDto(model);

        await uri.PutDto(dto, pipeline, cancellationToken);
    }

    public static async ValueTask DeleteAll(ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await LoggersUri.From(serviceUri).DeleteAll(pipeline, cancellationToken);

    public static async ValueTask WriteArtifacts(IEnumerable<LoggerModel> models, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await models.IterParallel(async model =>
        {
            await WriteInformationFile(model, serviceDirectory, cancellationToken);
        }, cancellationToken);

    private static async ValueTask WriteInformationFile(LoggerModel model, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        var informationFile = LoggerInformationFile.From(model.Name, serviceDirectory);
        var dto = GetDto(model);

        await informationFile.WriteDto(dto, cancellationToken);
    }

    public static async ValueTask ValidateExtractedArtifacts(Option<FrozenSet<LoggerName>> namesToExtract, ManagementServiceDirectory serviceDirectory, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var apimResources = await GetApimResources(serviceUri, pipeline, cancellationToken);
        var fileResources = await GetFileResources(serviceDirectory, cancellationToken);

        var expected = apimResources.WhereKey(name => ExtractorOptions.ShouldExtract(name, namesToExtract))
                                    .MapValue(NormalizeDto);
        var actual = fileResources.MapValue(NormalizeDto);

        actual.Should().BeEquivalentTo(expected);
    }

    private static async ValueTask<FrozenDictionary<LoggerName, LoggerDto>> GetApimResources(ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var uri = LoggersUri.From(serviceUri);

        return await uri.List(pipeline, cancellationToken)
                        .ToFrozenDictionary(cancellationToken);
    }

    private static async ValueTask<FrozenDictionary<LoggerName, LoggerDto>> GetFileResources(ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await LoggerModule.ListInformationFiles(serviceDirectory)
                              .ToAsyncEnumerable()
                              .SelectAwait(async file => (file.Parent.Name,
                                                          await file.ReadDto(cancellationToken)))
                              .ToFrozenDictionary(cancellationToken);

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

    public static async ValueTask ValidatePublisherChanges(ManagementServiceDirectory serviceDirectory, IDictionary<LoggerName, LoggerDto> overrides, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var fileResources = await GetFileResources(serviceDirectory, cancellationToken);
        await ValidatePublisherChanges(fileResources, overrides, serviceUri, pipeline, cancellationToken);
    }

    private static async ValueTask ValidatePublisherChanges(IDictionary<LoggerName, LoggerDto> fileResources, IDictionary<LoggerName, LoggerDto> overrides, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var apimResources = await GetApimResources(serviceUri, pipeline, cancellationToken);

        var expected = PublisherOptions.Override(fileResources, overrides)
                                       .MapValue(NormalizeDto);
        var actual = apimResources.MapValue(NormalizeDto);
        actual.Should().BeEquivalentTo(expected);
    }

    public static async ValueTask ValidatePublisherCommitChanges(CommitId commitId, ManagementServiceDirectory serviceDirectory, IDictionary<LoggerName, LoggerDto> overrides, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var fileResources = await GetFileResources(commitId, serviceDirectory, cancellationToken);
        await ValidatePublisherChanges(fileResources, overrides, serviceUri, pipeline, cancellationToken);
    }

    private static async ValueTask<FrozenDictionary<LoggerName, LoggerDto>> GetFileResources(CommitId commitId, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await Git.GetExistingFilesInCommit(serviceDirectory.ToDirectoryInfo(), commitId)
                 .ToAsyncEnumerable()
                 .Choose(file => LoggerInformationFile.TryParse(file, serviceDirectory))
                 .Choose(async file => await TryGetCommitResource(commitId, serviceDirectory, file, cancellationToken))
                 .ToFrozenDictionary(cancellationToken);

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
}
