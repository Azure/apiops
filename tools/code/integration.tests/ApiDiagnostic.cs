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

public delegate ValueTask PutApiDiagnosticModels(IEnumerable<ApiDiagnosticModel> models, ApiName apiName, ManagementServiceName serviceName, CancellationToken cancellationToken);
public delegate ValueTask ValidateExtractedApiDiagnostics(ApiName apiName, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);
public delegate ValueTask<FrozenDictionary<ApiDiagnosticName, ApiDiagnosticDto>> GetApimApiDiagnostics(ApiName apiName, ManagementServiceName serviceName, CancellationToken cancellationToken);
public delegate ValueTask<FrozenDictionary<ApiDiagnosticName, ApiDiagnosticDto>> GetFileApiDiagnostics(ApiName apiName, ManagementServiceDirectory serviceDirectory, Option<CommitId> commitIdOption, CancellationToken cancellationToken);
public delegate ValueTask WriteApiDiagnosticModels(IEnumerable<ApiDiagnosticModel> models, ApiName apiName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);
public delegate ValueTask ValidatePublishedApiDiagnostics(ApiName apiName, Option<CommitId> commitIdOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

internal static class ApiDiagnosticModule
{
    public static void ConfigurePutApiDiagnosticModels(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureGetManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutApiDiagnosticModels);
    }

    private static PutApiDiagnosticModels GetPutApiDiagnosticModels(IServiceProvider provider)
    {
        var getServiceUri = provider.GetRequiredService<GetManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (models, apiName, serviceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutApiDiagnosticModels));

            logger.LogInformation("Putting diagnostic models for API {ApiName} in {ServiceName}...", apiName, serviceName);

            await models.IterParallel(async model =>
            {
                await put(model, apiName, serviceName, cancellationToken);
            }, cancellationToken);
        };

        async ValueTask put(ApiDiagnosticModel model, ApiName apiName, ManagementServiceName serviceName, CancellationToken cancellationToken)
        {
            var serviceUri = getServiceUri(serviceName);
            var diagnosticUri = ApiDiagnosticUri.From(model.Name, apiName, serviceUri);
            var dto = getDto(model);

            await diagnosticUri.PutDto(dto, pipeline, cancellationToken);
        }

        static ApiDiagnosticDto getDto(ApiDiagnosticModel model) =>
            new()
            {
                Properties = new ApiDiagnosticDto.DiagnosticContract
                {
                    LoggerId = $"/loggers/{model.LoggerName}",
                    AlwaysLog = model.AlwaysLog.ValueUnsafe(),
                    Sampling = model.Sampling.Map(sampling => new ApiDiagnosticDto.SamplingSettings
                    {
                        SamplingType = sampling.Type,
                        Percentage = sampling.Percentage
                    }).ValueUnsafe()
                }
            };
    }

    public static void ConfigureValidateExtractedApiDiagnostics(IHostApplicationBuilder builder)
    {
        ConfigureGetApimApiDiagnostics(builder);
        ConfigureGetFileApiDiagnostics(builder);

        builder.Services.TryAddSingleton(GetValidateExtractedApiDiagnostics);
    }

    private static ValidateExtractedApiDiagnostics GetValidateExtractedApiDiagnostics(IServiceProvider provider)
    {
        var getApimResources = provider.GetRequiredService<GetApimApiDiagnostics>();
        var getFileResources = provider.GetRequiredService<GetFileApiDiagnostics>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (apiName, serviceName, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ValidateExtractedApiDiagnostics));

            logger.LogInformation("Validating extracted diagnostic models for API {ApiName} in {ServiceName}...", apiName, serviceName);

            var apimResources = await getApimResources(apiName, serviceName, cancellationToken);
            var fileResources = await getFileResources(apiName, serviceDirectory, Prelude.None, cancellationToken);

            var expected = apimResources.MapValue(normalizeDto)
                                        .ToFrozenDictionary();

            var actual = fileResources.MapValue(normalizeDto)
                                      .ToFrozenDictionary();

            actual.Should().BeEquivalentTo(expected);
        };

        static string normalizeDto(ApiDiagnosticDto dto) =>
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

    public static void ConfigureGetApimApiDiagnostics(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureGetManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetGetApimApiDiagnostics);
    }

    private static GetApimApiDiagnostics GetGetApimApiDiagnostics(IServiceProvider provider)
    {
        var getServiceUri = provider.GetRequiredService<GetManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (apiName, serviceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(GetApimApiDiagnostics));

            logger.LogInformation("Getting diagnostics from API {ApiName} in {ServiceName}...", apiName, serviceName);

            var serviceUri = getServiceUri(serviceName);
            var diagnosticsUri = ApiDiagnosticsUri.From(apiName, serviceUri);

            return await diagnosticsUri.List(pipeline, cancellationToken).ToFrozenDictionary(cancellationToken);
        };
    }

    public static void ConfigureGetFileApiDiagnostics(IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(GetGetFileApiDiagnostics);
    }

    private static GetFileApiDiagnostics GetGetFileApiDiagnostics(IServiceProvider provider)
    {
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (apiName, serviceDirectory, commitIdOption, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(GetFileApiDiagnostics));

            return await commitIdOption.Map(commitId => getWithCommit(apiName, serviceDirectory, commitId, cancellationToken))
                                       .IfNone(() => getWithoutCommit(apiName, serviceDirectory, cancellationToken));
        };

        async ValueTask<FrozenDictionary<ApiDiagnosticName, ApiDiagnosticDto>> getWithCommit(ApiName apiName, ManagementServiceDirectory serviceDirectory, CommitId commitId, CancellationToken cancellationToken)
        {
            logger.LogInformation("Getting diagnostics from API {ApiName} in {ServiceDirectory} as of commit {CommitId}...", apiName, serviceDirectory, commitId);

            return await Git.GetExistingFilesInCommit(serviceDirectory.ToDirectoryInfo(), commitId)
                            .ToAsyncEnumerable()
                            .Choose(file => ApiDiagnosticInformationFile.TryParse(file, serviceDirectory))
                            .Where(file => file.Parent.Parent.Parent.Name == apiName)
                            .Choose(async file => await tryGetCommitResource(commitId, serviceDirectory, file, cancellationToken))
                            .ToFrozenDictionary(cancellationToken);
        }

        static async ValueTask<Option<(ApiDiagnosticName, ApiDiagnosticDto)>> tryGetCommitResource(CommitId commitId, ManagementServiceDirectory serviceDirectory, ApiDiagnosticInformationFile file, CancellationToken cancellationToken)
        {
            var name = file.Parent.Name;
            var contentsOption = Git.TryGetFileContentsInCommit(serviceDirectory.ToDirectoryInfo(), file.ToFileInfo(), commitId);

            return await contentsOption.MapTask(async contents =>
            {
                using (contents)
                {
                    var data = await BinaryData.FromStreamAsync(contents, cancellationToken);
                    var dto = data.ToObjectFromJson<ApiDiagnosticDto>();
                    return (name, dto);
                }
            });
        }

        async ValueTask<FrozenDictionary<ApiDiagnosticName, ApiDiagnosticDto>> getWithoutCommit(ApiName apiName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
        {
            logger.LogInformation("Getting diagnostics from API {ApiName} in {ServiceDirectory}...", apiName, serviceDirectory);

            var informationFiles = common.ApiDiagnosticModule.ListInformationFiles(serviceDirectory);

            return await informationFiles.ToAsyncEnumerable()
                                         .Where(file => file.Parent.Parent.Parent.Name == apiName)
                                         .SelectAwait(async file => (file.Parent.Name,
                                                                     await file.ReadDto(cancellationToken)))
                                         .ToFrozenDictionary(cancellationToken);
        }
    }

    public static void ConfigureWriteApiDiagnosticModels(IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(GetWriteApiDiagnosticModels);
    }

    private static WriteApiDiagnosticModels GetWriteApiDiagnosticModels(IServiceProvider provider)
    {
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (models, apiName, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(WriteApiDiagnosticModels));

            logger.LogInformation("Writing diagnostic models for API {ApiName} in {ServiceDirectory}...", apiName, serviceDirectory);

            await models.IterParallel(async model =>
            {
                await writeInformationFile(model, apiName, serviceDirectory, cancellationToken);
            }, cancellationToken);
        };

        async ValueTask writeInformationFile(ApiDiagnosticModel model, ApiName apiName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
        {
            var informationFile = ApiDiagnosticInformationFile.From(model.Name, apiName, serviceDirectory);
            var dto = getDto(model);

            await informationFile.WriteDto(dto, cancellationToken);
        }

        static ApiDiagnosticDto getDto(ApiDiagnosticModel model) =>
            new()
            {
                Properties = new ApiDiagnosticDto.DiagnosticContract
                {
                    LoggerId = $"/loggers/{model.LoggerName}",
                    AlwaysLog = model.AlwaysLog.ValueUnsafe(),
                    Sampling = model.Sampling.Map(sampling => new ApiDiagnosticDto.SamplingSettings
                    {
                        SamplingType = sampling.Type,
                        Percentage = sampling.Percentage
                    }).ValueUnsafe()
                }
            };
    }
    public static void ConfigureValidatePublishedApiDiagnostics(IHostApplicationBuilder builder)
    {
        ConfigureGetApimApiDiagnostics(builder);
        ConfigureGetFileApiDiagnostics(builder);

        builder.Services.TryAddSingleton(GetValidatePublishedApiDiagnostics);
    }

    private static ValidatePublishedApiDiagnostics GetValidatePublishedApiDiagnostics(IServiceProvider provider)
    {
        var getApimResources = provider.GetRequiredService<GetApimApiDiagnostics>();
        var getFileResources = provider.GetRequiredService<GetFileApiDiagnostics>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (apiName, commitIdOption, serviceName, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ValidatePublishedApiDiagnostics));

            logger.LogInformation("Validating published diagnostics in API {ApiName} in {ServiceDirectory}...", apiName, serviceDirectory);

            var apimResources = await getApimResources(apiName, serviceName, cancellationToken);
            var fileResources = await getFileResources(apiName, serviceDirectory, commitIdOption, cancellationToken);

            var expected = fileResources.MapValue(normalizeDto)
                                        .ToFrozenDictionary();

            var actual = apimResources.MapValue(normalizeDto)
                                      .ToFrozenDictionary();

            actual.Should().BeEquivalentTo(expected);
        };

        static string normalizeDto(ApiDiagnosticDto dto) =>
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
}
//internal static class ApiDiagnosticModule
//{
//    public static async ValueTask Put(IEnumerable<ApiDiagnosticModel> models, ApiName apiName, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
//        await models.IterParallel(async model =>
//        {
//            await Put(model, apiName, serviceUri, pipeline, cancellationToken);
//        }, cancellationToken);

//    private static async ValueTask Put(ApiDiagnosticModel model, ApiName apiName, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
//    {
//        var uri = ApiDiagnosticUri.From(model.Name, apiName, serviceUri);
//        var dto = ModelToDto(model);
//        await uri.PutDto(dto, pipeline, cancellationToken);
//    }

//    private static ApiDiagnosticDto ModelToDto(ApiDiagnosticModel model) =>
//        new()
//        {
//            Properties = new ApiDiagnosticDto.DiagnosticContract
//            {
//                LoggerId = $"/loggers/{model.LoggerName}",
//                AlwaysLog = model.AlwaysLog.ValueUnsafe(),
//                Sampling = model.Sampling.Map(sampling => new ApiDiagnosticDto.SamplingSettings
//                {
//                    SamplingType = sampling.Type,
//                    Percentage = sampling.Percentage
//                }).ValueUnsafe()
//            }
//        };

//    public static async ValueTask WriteArtifacts(IEnumerable<ApiDiagnosticModel> models, ApiName apiName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
//        await models.IterParallel(async model =>
//        {
//            await WriteInformationFile(model, apiName, serviceDirectory, cancellationToken);
//        }, cancellationToken);

//    private static async ValueTask WriteInformationFile(ApiDiagnosticModel model, ApiName apiName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
//    {
//        var informationFile = ApiDiagnosticInformationFile.From(model.Name, apiName, serviceDirectory);
//        var dto = ModelToDto(model);
//        await informationFile.WriteDto(dto, cancellationToken);
//    }

//    public static async ValueTask ValidateExtractedArtifacts(ManagementServiceDirectory serviceDirectory, ApiName apiName, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
//    {
//        var apimResources = await GetApimResources(apiName, serviceUri, pipeline, cancellationToken);
//        var fileResources = await GetFileResources(apiName, serviceDirectory, cancellationToken);

//        var expected = apimResources.MapValue(NormalizeDto);
//        var actual = fileResources.MapValue(NormalizeDto);

//        actual.Should().BeEquivalentTo(expected);
//    }

//    private static async ValueTask<FrozenDictionary<ApiDiagnosticName, ApiDiagnosticDto>> GetApimResources(ApiName apiName, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
//    {
//        var uri = ApiDiagnosticsUri.From(apiName, serviceUri);

//        return await uri.List(pipeline, cancellationToken)
//                        .ToFrozenDictionary(cancellationToken);
//    }

//    private static async ValueTask<FrozenDictionary<ApiDiagnosticName, ApiDiagnosticDto>> GetFileResources(ApiName apiName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
//        await common.ApiDiagnosticModule.ListInformationFiles(serviceDirectory)
//                                        .Where(file => file.Parent.Parent.Parent.Name == apiName)
//                                        .ToAsyncEnumerable()
//                                        .SelectAwait(async file => (file.Parent.Name,
//                                                                    await file.ReadDto(cancellationToken)))
//                                        .ToFrozenDictionary(cancellationToken);

//    private static string NormalizeDto(ApiDiagnosticDto dto) =>
//        new
//        {
//            LoggerId = string.Join('/', dto.Properties.LoggerId?.Split('/')?.TakeLast(2)?.ToArray() ?? []),
//            AlwaysLog = dto.Properties.AlwaysLog ?? string.Empty,
//            Sampling = new
//            {
//                Type = dto.Properties.Sampling?.SamplingType ?? string.Empty,
//                Percentage = dto.Properties.Sampling?.Percentage ?? 0
//            }
//        }.ToString()!;

//    public static async ValueTask ValidatePublisherChanges(ApiName apiName, ManagementServiceDirectory serviceDirectory, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
//    {
//        var fileResources = await GetFileResources(apiName, serviceDirectory, cancellationToken);
//        await ValidatePublisherChanges(apiName, fileResources, serviceUri, pipeline, cancellationToken);
//    }

//    private static async ValueTask ValidatePublisherChanges(ApiName apiName, IDictionary<ApiDiagnosticName, ApiDiagnosticDto> fileResources, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
//    {
//        var apimResources = await GetApimResources(apiName, serviceUri, pipeline, cancellationToken);

//        var expected = fileResources.MapValue(NormalizeDto);
//        var actual = apimResources.MapValue(NormalizeDto);
//        actual.Should().BeEquivalentTo(expected);
//    }

//    public static async ValueTask ValidatePublisherCommitChanges(ApiName apiName, CommitId commitId, ManagementServiceDirectory serviceDirectory, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
//    {
//        var fileResources = await GetFileResources(apiName, commitId, serviceDirectory, cancellationToken);
//        await ValidatePublisherChanges(apiName, fileResources, serviceUri, pipeline, cancellationToken);
//    }

//    private static async ValueTask<FrozenDictionary<ApiDiagnosticName, ApiDiagnosticDto>> GetFileResources(ApiName apiName, CommitId commitId, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
//        await Git.GetExistingFilesInCommit(serviceDirectory.ToDirectoryInfo(), commitId)
//                 .ToAsyncEnumerable()
//                 .Choose(file => ApiDiagnosticInformationFile.TryParse(file, serviceDirectory))
//                 .Where(file => file.Parent.Parent.Parent.Name == apiName)
//                 .Choose(async file => await TryGetCommitResource(commitId, serviceDirectory, file, cancellationToken))
//                 .ToFrozenDictionary(cancellationToken);

//    private static async ValueTask<Option<(ApiDiagnosticName name, ApiDiagnosticDto dto)>> TryGetCommitResource(CommitId commitId, ManagementServiceDirectory serviceDirectory, ApiDiagnosticInformationFile file, CancellationToken cancellationToken)
//    {
//        var name = file.Parent.Name;
//        var contentsOption = Git.TryGetFileContentsInCommit(serviceDirectory.ToDirectoryInfo(), file.ToFileInfo(), commitId);

//        return await contentsOption.MapTask(async contents =>
//        {
//            using (contents)
//            {
//                var data = await BinaryData.FromStreamAsync(contents, cancellationToken);
//                var dto = data.ToObjectFromJson<ApiDiagnosticDto>();
//                return (name, dto);
//            }
//        });
//    }
//}
