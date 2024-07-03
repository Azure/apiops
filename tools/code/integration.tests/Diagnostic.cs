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

internal delegate ValueTask DeleteAllDiagnostics(ManagementServiceName serviceName, CancellationToken cancellationToken);

internal delegate ValueTask PutDiagnosticModels(IEnumerable<DiagnosticModel> models, ManagementServiceName serviceName, CancellationToken cancellationToken);

internal delegate ValueTask ValidateExtractedDiagnostics(Option<FrozenSet<DiagnosticName>> diagnosticNamesOption, Option<FrozenSet<LoggerName>> loggerNamesOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

file delegate ValueTask<FrozenDictionary<DiagnosticName, DiagnosticDto>> GetApimDiagnostics(ManagementServiceName serviceName, CancellationToken cancellationToken);

file delegate ValueTask<FrozenDictionary<DiagnosticName, DiagnosticDto>> GetFileDiagnostics(ManagementServiceDirectory serviceDirectory, Option<CommitId> commitIdOption, CancellationToken cancellationToken);

internal delegate ValueTask WriteDiagnosticModels(IEnumerable<DiagnosticModel> models, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

internal delegate ValueTask ValidatePublishedDiagnostics(IDictionary<DiagnosticName, DiagnosticDto> overrides, Option<CommitId> commitIdOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

file sealed class DeleteAllDiagnosticsHandler(ILogger<DeleteAllDiagnostics> logger, GetManagementServiceUri getServiceUri, HttpPipeline pipeline, ActivitySource activitySource)
{
    public async ValueTask Handle(ManagementServiceName serviceName, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(DeleteAllDiagnostics));

        logger.LogInformation("Deleting all diagnostics in {ServiceName}...", serviceName);
        var serviceUri = getServiceUri(serviceName);
        await DiagnosticsUri.From(serviceUri).DeleteAll(pipeline, cancellationToken);
    }
}

file sealed class PutDiagnosticModelsHandler(ILogger<PutDiagnosticModels> logger, GetManagementServiceUri getServiceUri, HttpPipeline pipeline, ActivitySource activitySource)
{
    public async ValueTask Handle(IEnumerable<DiagnosticModel> models, ManagementServiceName serviceName, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(PutDiagnosticModels));

        logger.LogInformation("Putting diagnostic models in {ServiceName}...", serviceName);
        await models.IterParallel(async model =>
        {
            await Put(model, serviceName, cancellationToken);
        }, cancellationToken);
    }

    private async ValueTask Put(DiagnosticModel model, ManagementServiceName serviceName, CancellationToken cancellationToken)
    {
        var serviceUri = getServiceUri(serviceName);
        var uri = DiagnosticUri.From(model.Name, serviceUri);
        var dto = GetDto(model);

        await uri.PutDto(dto, pipeline, cancellationToken);
    }

    private static DiagnosticDto GetDto(DiagnosticModel model) =>
        new()
        {
            Properties = new DiagnosticDto.DiagnosticContract
            {
                LoggerId = $"/loggers/{model.LoggerName}",
                AlwaysLog = model.AlwaysLog.ValueUnsafe(),
                Sampling = model.Sampling.Map(sampling => new DiagnosticDto.SamplingSettings
                {
                    SamplingType = sampling.Type,
                    Percentage = sampling.Percentage
                }).ValueUnsafe()
            }
        };
}

file sealed class ValidateExtractedDiagnosticsHandler(ILogger<ValidateExtractedDiagnostics> logger, GetApimDiagnostics getApimResources, GetFileDiagnostics getFileResources, ActivitySource activitySource)
{
    public async ValueTask Handle(Option<FrozenSet<DiagnosticName>> diagnosticNamesOption, Option<FrozenSet<LoggerName>> loggerNamesOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(ValidateExtractedDiagnostics));

        logger.LogInformation("Validating extracted diagnostics in {ServiceName}...", serviceName);
        var apimResources = await getApimResources(serviceName, cancellationToken);
        var fileResources = await getFileResources(serviceDirectory, Prelude.None, cancellationToken);

        var expected = apimResources.WhereKey(name => ExtractorOptions.ShouldExtract(name, diagnosticNamesOption))
                                    .WhereValue(dto => DiagnosticModule.TryGetLoggerName(dto)
                                                                       .Map(name => ExtractorOptions.ShouldExtract(name, loggerNamesOption))
                                                                       .IfNone(true))
                                    .MapValue(NormalizeDto);
        var actual = fileResources.MapValue(NormalizeDto);

        actual.Should().BeEquivalentTo(expected);
    }

    private static string NormalizeDto(DiagnosticDto dto) =>
        new
        {
            LoggerId = string.Join('/', dto.Properties.LoggerId?.Split('/')?.TakeLast(2)?.ToArray() ?? []),
            AlwaysLog = dto.Properties.AlwaysLog ?? string.Empty,
            Sampling = new
            {
                Type = dto.Properties.Sampling?.SamplingType ?? string.Empty,
                Percentage = dto.Properties.Sampling?.Percentage ?? 0
            }
        }.ToString()!;
}

file sealed class GetApimDiagnosticsHandler(ILogger<GetApimDiagnostics> logger, GetManagementServiceUri getServiceUri, HttpPipeline pipeline, ActivitySource activitySource)
{
    public async ValueTask<FrozenDictionary<DiagnosticName, DiagnosticDto>> Handle(ManagementServiceName serviceName, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(GetApimDiagnostics));

        logger.LogInformation("Getting diagnostics from {ServiceName}...", serviceName);

        var serviceUri = getServiceUri(serviceName);
        var uri = DiagnosticsUri.From(serviceUri);

        return await uri.List(pipeline, cancellationToken)
                        .ToFrozenDictionary(cancellationToken);
    }
}

file sealed class GetFileDiagnosticsHandler(ILogger<GetFileDiagnostics> logger, ActivitySource activitySource)
{
    public async ValueTask<FrozenDictionary<DiagnosticName, DiagnosticDto>> Handle(ManagementServiceDirectory serviceDirectory, Option<CommitId> commitIdOption, CancellationToken cancellationToken) =>
        await commitIdOption.Map(commitId => GetWithCommit(serviceDirectory, commitId, cancellationToken))
                           .IfNone(() => GetWithoutCommit(serviceDirectory, cancellationToken));

    private async ValueTask<FrozenDictionary<DiagnosticName, DiagnosticDto>> GetWithCommit(ManagementServiceDirectory serviceDirectory, CommitId commitId, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(GetFileDiagnostics));

        logger.LogInformation("Getting diagnostics from {ServiceDirectory} as of commit {CommitId}...", serviceDirectory, commitId);

        return await Git.GetExistingFilesInCommit(serviceDirectory.ToDirectoryInfo(), commitId)
                        .ToAsyncEnumerable()
                        .Choose(file => DiagnosticInformationFile.TryParse(file, serviceDirectory))
                        .Choose(async file => await TryGetCommitResource(commitId, serviceDirectory, file, cancellationToken))
                        .ToFrozenDictionary(cancellationToken);
    }

    private static async ValueTask<Option<(DiagnosticName name, DiagnosticDto dto)>> TryGetCommitResource(CommitId commitId, ManagementServiceDirectory serviceDirectory, DiagnosticInformationFile file, CancellationToken cancellationToken)
    {
        var name = file.Parent.Name;
        var contentsOption = Git.TryGetFileContentsInCommit(serviceDirectory.ToDirectoryInfo(), file.ToFileInfo(), commitId);

        return await contentsOption.MapTask(async contents =>
        {
            using (contents)
            {
                var data = await BinaryData.FromStreamAsync(contents, cancellationToken);
                var dto = data.ToObjectFromJson<DiagnosticDto>();
                return (name, dto);
            }
        });
    }

    private async ValueTask<FrozenDictionary<DiagnosticName, DiagnosticDto>> GetWithoutCommit(ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(GetFileDiagnostics));

        logger.LogInformation("Getting diagnostics from {ServiceDirectory}...", serviceDirectory);

        return await DiagnosticModule.ListInformationFiles(serviceDirectory)
                              .ToAsyncEnumerable()
                              .SelectAwait(async file => (file.Parent.Name,
                                                          await file.ReadDto(cancellationToken)))
                              .ToFrozenDictionary(cancellationToken);
    }
}

file sealed class WriteDiagnosticModelsHandler(ILogger<WriteDiagnosticModels> logger, ActivitySource activitySource)
{
    public async ValueTask Handle(IEnumerable<DiagnosticModel> models, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(WriteDiagnosticModels));

        logger.LogInformation("Writing diagnostic models to {ServiceDirectory}...", serviceDirectory);
        await models.IterParallel(async model =>
        {
            await WriteInformationFile(model, serviceDirectory, cancellationToken);
        }, cancellationToken);
    }

    private static async ValueTask WriteInformationFile(DiagnosticModel model, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        var informationFile = DiagnosticInformationFile.From(model.Name, serviceDirectory);
        var dto = GetDto(model);

        await informationFile.WriteDto(dto, cancellationToken);
    }

    private static DiagnosticDto GetDto(DiagnosticModel model) =>
        new()
        {
            Properties = new DiagnosticDto.DiagnosticContract
            {
                LoggerId = $"/loggers/{model.LoggerName}",
                AlwaysLog = model.AlwaysLog.ValueUnsafe(),
                Sampling = model.Sampling.Map(sampling => new DiagnosticDto.SamplingSettings
                {
                    SamplingType = sampling.Type,
                    Percentage = sampling.Percentage
                }).ValueUnsafe()
            }
        };
}

file sealed class ValidatePublishedDiagnosticsHandler(ILogger<ValidatePublishedDiagnostics> logger, GetFileDiagnostics getFileResources, GetApimDiagnostics getApimResources, ActivitySource activitySource)
{
    public async ValueTask Handle(IDictionary<DiagnosticName, DiagnosticDto> overrides, Option<CommitId> commitIdOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(ValidatePublishedDiagnostics));

        logger.LogInformation("Validating published diagnostics in {ServiceDirectory}...", serviceDirectory);

        var apimResources = await getApimResources(serviceName, cancellationToken);
        var fileResources = await getFileResources(serviceDirectory, commitIdOption, cancellationToken);

        var expected = PublisherOptions.Override(fileResources, overrides)
                                       .MapValue(NormalizeDto);
        var actual = apimResources.MapValue(NormalizeDto);

        actual.Should().BeEquivalentTo(expected);
    }

    private static string NormalizeDto(DiagnosticDto dto) =>
        new
        {
            LoggerId = string.Join('/', dto.Properties.LoggerId?.Split('/')?.TakeLast(2)?.ToArray() ?? []),
            AlwaysLog = dto.Properties.AlwaysLog ?? string.Empty,
            Sampling = new
            {
                Type = dto.Properties.Sampling?.SamplingType ?? string.Empty,
                Percentage = dto.Properties.Sampling?.Percentage ?? 0
            }
        }.ToString()!;
}

internal static class DiagnosticServices
{
    public static void ConfigureDeleteAllDiagnostics(IServiceCollection services)
    {
        ManagementServices.ConfigureGetManagementServiceUri(services);

        services.TryAddSingleton<DeleteAllDiagnosticsHandler>();
        services.TryAddSingleton<DeleteAllDiagnostics>(provider => provider.GetRequiredService<DeleteAllDiagnosticsHandler>().Handle);
    }

    public static void ConfigurePutDiagnosticModels(IServiceCollection services)
    {
        ManagementServices.ConfigureGetManagementServiceUri(services);

        services.TryAddSingleton<PutDiagnosticModelsHandler>();
        services.TryAddSingleton<PutDiagnosticModels>(provider => provider.GetRequiredService<PutDiagnosticModelsHandler>().Handle);
    }

    public static void ConfigureValidateExtractedDiagnostics(IServiceCollection services)
    {
        ConfigureGetApimDiagnostics(services);
        ConfigureGetFileDiagnostics(services);

        services.TryAddSingleton<ValidateExtractedDiagnosticsHandler>();
        services.TryAddSingleton<ValidateExtractedDiagnostics>(provider => provider.GetRequiredService<ValidateExtractedDiagnosticsHandler>().Handle);
    }

    private static void ConfigureGetApimDiagnostics(IServiceCollection services)
    {
        ManagementServices.ConfigureGetManagementServiceUri(services);

        services.TryAddSingleton<GetApimDiagnosticsHandler>();
        services.TryAddSingleton<GetApimDiagnostics>(provider => provider.GetRequiredService<GetApimDiagnosticsHandler>().Handle);
    }

    private static void ConfigureGetFileDiagnostics(IServiceCollection services)
    {
        services.TryAddSingleton<GetFileDiagnosticsHandler>();
        services.TryAddSingleton<GetFileDiagnostics>(provider => provider.GetRequiredService<GetFileDiagnosticsHandler>().Handle);
    }

    public static void ConfigureWriteDiagnosticModels(IServiceCollection services)
    {
        services.TryAddSingleton<WriteDiagnosticModelsHandler>();
        services.TryAddSingleton<WriteDiagnosticModels>(provider => provider.GetRequiredService<WriteDiagnosticModelsHandler>().Handle);
    }

    public static void ConfigureValidatePublishedDiagnostics(IServiceCollection services)
    {
        ConfigureGetFileDiagnostics(services);
        ConfigureGetApimDiagnostics(services);

        services.TryAddSingleton<ValidatePublishedDiagnosticsHandler>();
        services.TryAddSingleton<ValidatePublishedDiagnostics>(provider => provider.GetRequiredService<ValidatePublishedDiagnosticsHandler>().Handle);
    }
}

internal static class Diagnostic
{
    public static Gen<DiagnosticModel> GenerateUpdate(DiagnosticModel original) =>
        from alwaysLog in Gen.Const("allErrors").OptionOf()
        from sampling in DiagnosticSampling.Generate().OptionOf()
        select original with
        {
            AlwaysLog = alwaysLog,
            Sampling = sampling
        };

    public static Gen<DiagnosticDto> GenerateOverride(DiagnosticDto original) =>
        from alwaysLog in Gen.Const("allErrors").OptionOf()
        from sampling in DiagnosticSampling.Generate().OptionOf()
        select new DiagnosticDto
        {
            Properties = new DiagnosticDto.DiagnosticContract
            {
                AlwaysLog = alwaysLog.ValueUnsafe(),
                Sampling = sampling.Map(sampling => new DiagnosticDto.SamplingSettings
                {
                    SamplingType = sampling.Type,
                    Percentage = sampling.Percentage
                }).ValueUnsafe()
            }
        };

    public static FrozenDictionary<DiagnosticName, DiagnosticDto> GetDtoDictionary(IEnumerable<DiagnosticModel> models) =>
        models.ToFrozenDictionary(model => model.Name, GetDto);

    private static DiagnosticDto GetDto(DiagnosticModel model) =>
        new()
        {
            Properties = new DiagnosticDto.DiagnosticContract
            {
                LoggerId = $"/loggers/{model.LoggerName}",
                AlwaysLog = model.AlwaysLog.ValueUnsafe(),
                Sampling = model.Sampling.Map(sampling => new DiagnosticDto.SamplingSettings
                {
                    SamplingType = sampling.Type,
                    Percentage = sampling.Percentage
                }).ValueUnsafe()
            }
        };
}
