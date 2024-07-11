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

public delegate ValueTask DeleteAllDiagnostics(ManagementServiceName serviceName, CancellationToken cancellationToken);
public delegate ValueTask PutDiagnosticModels(IEnumerable<DiagnosticModel> models, ManagementServiceName serviceName, CancellationToken cancellationToken);
public delegate ValueTask ValidateExtractedDiagnostics(Option<FrozenSet<DiagnosticName>> diagnosticNamesOption, Option<FrozenSet<LoggerName>> loggerNamesOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);
public delegate ValueTask<FrozenDictionary<DiagnosticName, DiagnosticDto>> GetApimDiagnostics(ManagementServiceName serviceName, CancellationToken cancellationToken);
public delegate ValueTask<FrozenDictionary<DiagnosticName, DiagnosticDto>> GetFileDiagnostics(ManagementServiceDirectory serviceDirectory, Option<CommitId> commitIdOption, CancellationToken cancellationToken);
public delegate ValueTask WriteDiagnosticModels(IEnumerable<DiagnosticModel> models, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);
public delegate ValueTask ValidatePublishedDiagnostics(IDictionary<DiagnosticName, DiagnosticDto> overrides, Option<CommitId> commitIdOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

public static class DiagnosticModule
{
    public static void ConfigureDeleteAllDiagnostics(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureGetManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteAllDiagnostics);
    }

    private static DeleteAllDiagnostics GetDeleteAllDiagnostics(IServiceProvider provider)
    {
        var getServiceUri = provider.GetRequiredService<GetManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (serviceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteAllDiagnostics));

            logger.LogInformation("Deleting all diagnostics in {ServiceName}...", serviceName);

            var serviceUri = getServiceUri(serviceName);

            await DiagnosticsUri.From(serviceUri)
                                .DeleteAll(pipeline, cancellationToken);
        };
    }

    public static void ConfigurePutDiagnosticModels(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureGetManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutDiagnosticModels);
    }

    private static PutDiagnosticModels GetPutDiagnosticModels(IServiceProvider provider)
    {
        var getServiceUri = provider.GetRequiredService<GetManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (models, serviceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutDiagnosticModels));

            logger.LogInformation("Putting diagnostic models in {ServiceName}...", serviceName);

            await models.IterParallel(async model =>
            {
                await put(model, serviceName, cancellationToken);
            }, cancellationToken);
        };

        async ValueTask put(DiagnosticModel model, ManagementServiceName serviceName, CancellationToken cancellationToken)
        {
            var serviceUri = getServiceUri(serviceName);

            var dto = getDto(model);

            await DiagnosticUri.From(model.Name, serviceUri)
                            .PutDto(dto, pipeline, cancellationToken);
        }

        static DiagnosticDto getDto(DiagnosticModel model) =>
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

    public static void ConfigureValidateExtractedDiagnostics(IHostApplicationBuilder builder)
    {
        ConfigureGetApimDiagnostics(builder);
        ConfigureGetFileDiagnostics(builder);

        builder.Services.TryAddSingleton(GetValidateExtractedDiagnostics);
    }

    private static ValidateExtractedDiagnostics GetValidateExtractedDiagnostics(IServiceProvider provider)
    {
        var getApimResources = provider.GetRequiredService<GetApimDiagnostics>();
        var tryGetApimGraphQlSchema = provider.GetRequiredService<TryGetApimGraphQlSchema>();
        var getFileResources = provider.GetRequiredService<GetFileDiagnostics>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (namesFilterOption, loggerNamesFilterOption, serviceName, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ValidateExtractedDiagnostics));

            logger.LogInformation("Validating extracted diagnostics in {ServiceName}...", serviceName);

            var apimResources = await getApimResources(serviceName, cancellationToken);
            var fileResources = await getFileResources(serviceDirectory, Prelude.None, cancellationToken);

            var expected = apimResources.WhereKey(name => ExtractorOptions.ShouldExtract(name, namesFilterOption))
                                        .WhereValue(dto => common.DiagnosticModule.TryGetLoggerName(dto)
                                                                                  .Map(name => ExtractorOptions.ShouldExtract(name, loggerNamesFilterOption))
                                                                                  .IfNone(true))
                                        .MapValue(normalizeDto)
                                        .ToFrozenDictionary();

            var actual = fileResources.MapValue(normalizeDto)
                                      .ToFrozenDictionary();

            actual.Should().BeEquivalentTo(expected);
        };

        static string normalizeDto(DiagnosticDto dto) =>
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

    public static void ConfigureGetApimDiagnostics(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureGetManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetGetApimDiagnostics);
    }

    private static GetApimDiagnostics GetGetApimDiagnostics(IServiceProvider provider)
    {
        var getServiceUri = provider.GetRequiredService<GetManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (serviceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(GetApimDiagnostics));

            logger.LogInformation("Getting diagnostics from {ServiceName}...", serviceName);

            var serviceUri = getServiceUri(serviceName);

            return await DiagnosticsUri.From(serviceUri)
                                       .List(pipeline, cancellationToken)
                                       .ToFrozenDictionary(cancellationToken);
        };
    }

    public static void ConfigureGetFileDiagnostics(IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(GetGetFileDiagnostics);
    }

    private static GetFileDiagnostics GetGetFileDiagnostics(IServiceProvider provider)
    {
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (serviceDirectory, commitIdOption, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(GetFileDiagnostics));

            return await commitIdOption.Map(commitId => getWithCommit(serviceDirectory, commitId, cancellationToken))
                                       .IfNone(() => getWithoutCommit(serviceDirectory, cancellationToken));
        };

        async ValueTask<FrozenDictionary<DiagnosticName, DiagnosticDto>> getWithCommit(ManagementServiceDirectory serviceDirectory, CommitId commitId, CancellationToken cancellationToken)
        {
            using var _ = activitySource.StartActivity(nameof(GetFileDiagnostics));

            logger.LogInformation("Getting diagnostics from {ServiceDirectory} as of commit {CommitId}...", serviceDirectory, commitId);

            return await Git.GetExistingFilesInCommit(serviceDirectory.ToDirectoryInfo(), commitId)
                            .ToAsyncEnumerable()
                            .Choose(file => DiagnosticInformationFile.TryParse(file, serviceDirectory))
                            .Choose(async file => await tryGetCommitResource(commitId, serviceDirectory, file, cancellationToken))
                            .ToFrozenDictionary(cancellationToken);
        }

        static async ValueTask<Option<(DiagnosticName name, DiagnosticDto dto)>> tryGetCommitResource(CommitId commitId, ManagementServiceDirectory serviceDirectory, DiagnosticInformationFile file, CancellationToken cancellationToken)
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

        async ValueTask<FrozenDictionary<DiagnosticName, DiagnosticDto>> getWithoutCommit(ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
        {
            logger.LogInformation("Getting diagnostics from {ServiceDirectory}...", serviceDirectory);

            return await common.DiagnosticModule.ListInformationFiles(serviceDirectory)
                                             .ToAsyncEnumerable()
                                             .SelectAwait(async file => (file.Parent.Name,
                                                                         await file.ReadDto(cancellationToken)))
                                             .ToFrozenDictionary(cancellationToken);
        }
    }

    public static void ConfigureWriteDiagnosticModels(IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(GetWriteDiagnosticModels);
    }

    private static WriteDiagnosticModels GetWriteDiagnosticModels(IServiceProvider provider)
    {
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (models, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(WriteDiagnosticModels));

            logger.LogInformation("Writing diagnostic models to {ServiceDirectory}...", serviceDirectory);

            await models.IterParallel(async model =>
            {
                await writeInformationFile(model, serviceDirectory, cancellationToken);
            }, cancellationToken);
        };

        static async ValueTask writeInformationFile(DiagnosticModel model, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
        {
            var informationFile = DiagnosticInformationFile.From(model.Name, serviceDirectory);
            var dto = getDto(model);

            await informationFile.WriteDto(dto, cancellationToken);
        }

        static DiagnosticDto getDto(DiagnosticModel model) =>
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

    public static void ConfigureValidatePublishedDiagnostics(IHostApplicationBuilder builder)
    {
        ConfigureGetFileDiagnostics(builder);
        ConfigureGetApimDiagnostics(builder);

        builder.Services.TryAddSingleton(GetValidatePublishedDiagnostics);
    }

    private static ValidatePublishedDiagnostics GetValidatePublishedDiagnostics(IServiceProvider provider)
    {
        var getFileResources = provider.GetRequiredService<GetFileDiagnostics>();
        var getApimResources = provider.GetRequiredService<GetApimDiagnostics>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (overrides, commitIdOption, serviceName, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ValidatePublishedDiagnostics));

            logger.LogInformation("Validating published diagnostics in {ServiceDirectory}...", serviceDirectory);

            var apimResources = await getApimResources(serviceName, cancellationToken);
            var fileResources = await getFileResources(serviceDirectory, commitIdOption, cancellationToken);

            var expected = PublisherOptions.Override(fileResources, overrides)
                                           .MapValue(normalizeDto)
                                           .ToFrozenDictionary();

            var actual = apimResources.MapValue(normalizeDto)
                                      .ToFrozenDictionary();

            actual.Should().BeEquivalentTo(expected);
        };

        static string normalizeDto(DiagnosticDto dto) =>
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