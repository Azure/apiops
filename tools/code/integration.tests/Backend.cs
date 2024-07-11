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

public delegate ValueTask DeleteAllBackends(ManagementServiceName serviceName, CancellationToken cancellationToken);
public delegate ValueTask PutBackendModels(IEnumerable<BackendModel> models, ManagementServiceName serviceName, CancellationToken cancellationToken);
public delegate ValueTask ValidateExtractedBackends(Option<FrozenSet<BackendName>> backendNamesOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);
public delegate ValueTask<FrozenDictionary<BackendName, BackendDto>> GetApimBackends(ManagementServiceName serviceName, CancellationToken cancellationToken);
public delegate ValueTask<FrozenDictionary<BackendName, BackendDto>> GetFileBackends(ManagementServiceDirectory serviceDirectory, Option<CommitId> commitIdOption, CancellationToken cancellationToken);
public delegate ValueTask WriteBackendModels(IEnumerable<BackendModel> models, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);
public delegate ValueTask ValidatePublishedBackends(IDictionary<BackendName, BackendDto> overrides, Option<CommitId> commitIdOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

public static class BackendModule
{
    public static void ConfigureDeleteAllBackends(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureGetManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteAllBackends);
    }

    private static DeleteAllBackends GetDeleteAllBackends(IServiceProvider provider)
    {
        var getServiceUri = provider.GetRequiredService<GetManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (serviceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteAllBackends));

            logger.LogInformation("Deleting all backends in {ServiceName}...", serviceName);

            var serviceUri = getServiceUri(serviceName);

            await BackendsUri.From(serviceUri)
                             .DeleteAll(pipeline, cancellationToken);
        };
    }

    public static void ConfigurePutBackendModels(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureGetManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutBackendModels);
    }

    private static PutBackendModels GetPutBackendModels(IServiceProvider provider)
    {
        var getServiceUri = provider.GetRequiredService<GetManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (models, serviceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutBackendModels));

            logger.LogInformation("Putting backend models in {ServiceName}...", serviceName);

            await models.IterParallel(async model =>
            {
                await put(model, serviceName, cancellationToken);
            }, cancellationToken);
        };

        async ValueTask put(BackendModel model, ManagementServiceName serviceName, CancellationToken cancellationToken)
        {
            var serviceUri = getServiceUri(serviceName);

            var dto = getDto(model);

            await BackendUri.From(model.Name, serviceUri)
                            .PutDto(dto, pipeline, cancellationToken);
        }

        static BackendDto getDto(BackendModel model) =>
            new()
            {
                Properties = new BackendDto.BackendContract
                {
                    Url = model.Url.ToString(),
                    Description = model.Description.ValueUnsafe(),
                    Protocol = model.Protocol
                }
            };
    }

    public static void ConfigureValidateExtractedBackends(IHostApplicationBuilder builder)
    {
        ConfigureGetApimBackends(builder);
        ConfigureGetFileBackends(builder);

        builder.Services.TryAddSingleton(GetValidateExtractedBackends);
    }

    private static ValidateExtractedBackends GetValidateExtractedBackends(IServiceProvider provider)
    {
        var getApimResources = provider.GetRequiredService<GetApimBackends>();
        var tryGetApimGraphQlSchema = provider.GetRequiredService<TryGetApimGraphQlSchema>();
        var getFileResources = provider.GetRequiredService<GetFileBackends>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (namesFilterOption, serviceName, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ValidateExtractedBackends));

            logger.LogInformation("Validating extracted backends in {ServiceName}...", serviceName);

            var apimResources = await getApimResources(serviceName, cancellationToken);
            var fileResources = await getFileResources(serviceDirectory, Prelude.None, cancellationToken);

            var expected = apimResources.WhereKey(name => ExtractorOptions.ShouldExtract(name, namesFilterOption))
                                        .MapValue(normalizeDto)
                                        .ToFrozenDictionary();

            var actual = fileResources.MapValue(normalizeDto)
                                      .ToFrozenDictionary();

            actual.Should().BeEquivalentTo(expected);
        };

        static string normalizeDto(BackendDto dto) =>
            new
            {
                Url = dto.Properties.Url ?? string.Empty,
                Description = dto.Properties.Description ?? string.Empty,
                Protocol = dto.Properties.Protocol ?? string.Empty
            }.ToString()!;
    }

    public static void ConfigureGetApimBackends(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureGetManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetGetApimBackends);
    }

    private static GetApimBackends GetGetApimBackends(IServiceProvider provider)
    {
        var getServiceUri = provider.GetRequiredService<GetManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (serviceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(GetApimBackends));

            logger.LogInformation("Getting backends from {ServiceName}...", serviceName);

            var serviceUri = getServiceUri(serviceName);

            return await BackendsUri.From(serviceUri)
                                    .List(pipeline, cancellationToken)
                                    .ToFrozenDictionary(cancellationToken);
        };
    }

    public static void ConfigureGetFileBackends(IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(GetGetFileBackends);
    }

    private static GetFileBackends GetGetFileBackends(IServiceProvider provider)
    {
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (serviceDirectory, commitIdOption, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(GetFileBackends));

            return await commitIdOption.Map(commitId => getWithCommit(serviceDirectory, commitId, cancellationToken))
                                       .IfNone(() => getWithoutCommit(serviceDirectory, cancellationToken));
        };

        async ValueTask<FrozenDictionary<BackendName, BackendDto>> getWithCommit(ManagementServiceDirectory serviceDirectory, CommitId commitId, CancellationToken cancellationToken)
        {
            using var _ = activitySource.StartActivity(nameof(GetFileBackends));

            logger.LogInformation("Getting backends from {ServiceDirectory} as of commit {CommitId}...", serviceDirectory, commitId);

            return await Git.GetExistingFilesInCommit(serviceDirectory.ToDirectoryInfo(), commitId)
                            .ToAsyncEnumerable()
                            .Choose(file => BackendInformationFile.TryParse(file, serviceDirectory))
                            .Choose(async file => await tryGetCommitResource(commitId, serviceDirectory, file, cancellationToken))
                            .ToFrozenDictionary(cancellationToken);
        }

        static async ValueTask<Option<(BackendName name, BackendDto dto)>> tryGetCommitResource(CommitId commitId, ManagementServiceDirectory serviceDirectory, BackendInformationFile file, CancellationToken cancellationToken)
        {
            var name = file.Parent.Name;
            var contentsOption = Git.TryGetFileContentsInCommit(serviceDirectory.ToDirectoryInfo(), file.ToFileInfo(), commitId);

            return await contentsOption.MapTask(async contents =>
            {
                using (contents)
                {
                    var data = await BinaryData.FromStreamAsync(contents, cancellationToken);
                    var dto = data.ToObjectFromJson<BackendDto>();
                    return (name, dto);
                }
            });
        }

        async ValueTask<FrozenDictionary<BackendName, BackendDto>> getWithoutCommit(ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
        {
            logger.LogInformation("Getting backends from {ServiceDirectory}...", serviceDirectory);

            return await common.BackendModule.ListInformationFiles(serviceDirectory)
                                             .ToAsyncEnumerable()
                                             .SelectAwait(async file => (file.Parent.Name,
                                                                         await file.ReadDto(cancellationToken)))
                                             .ToFrozenDictionary(cancellationToken);
        }
    }

    public static void ConfigureWriteBackendModels(IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(GetWriteBackendModels);
    }

    private static WriteBackendModels GetWriteBackendModels(IServiceProvider provider)
    {
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (models, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(WriteBackendModels));

            logger.LogInformation("Writing backend models to {ServiceDirectory}...", serviceDirectory);

            await models.IterParallel(async model =>
            {
                await writeInformationFile(model, serviceDirectory, cancellationToken);
            }, cancellationToken);
        };

        static async ValueTask writeInformationFile(BackendModel model, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
        {
            var informationFile = BackendInformationFile.From(model.Name, serviceDirectory);
            var dto = getDto(model);

            await informationFile.WriteDto(dto, cancellationToken);
        }

        static BackendDto getDto(BackendModel model) =>
            new()
            {
                Properties = new BackendDto.BackendContract
                {
                    Url = model.Url.ToString(),
                    Description = model.Description.ValueUnsafe(),
                    Protocol = model.Protocol
                }
            };
    }

    public static void ConfigureValidatePublishedBackends(IHostApplicationBuilder builder)
    {
        ConfigureGetFileBackends(builder);
        ConfigureGetApimBackends(builder);

        builder.Services.TryAddSingleton(GetValidatePublishedBackends);
    }

    private static ValidatePublishedBackends GetValidatePublishedBackends(IServiceProvider provider)
    {
        var getFileResources = provider.GetRequiredService<GetFileBackends>();
        var getApimResources = provider.GetRequiredService<GetApimBackends>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (overrides, commitIdOption, serviceName, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ValidatePublishedBackends));

            logger.LogInformation("Validating published backends in {ServiceDirectory}...", serviceDirectory);

            var apimResources = await getApimResources(serviceName, cancellationToken);
            var fileResources = await getFileResources(serviceDirectory, commitIdOption, cancellationToken);

            var expected = PublisherOptions.Override(fileResources, overrides)
                                           .MapValue(normalizeDto)
                                           .ToFrozenDictionary();

            var actual = apimResources.MapValue(normalizeDto)
                                      .ToFrozenDictionary();

            actual.Should().BeEquivalentTo(expected);
        };

        static string normalizeDto(BackendDto dto) =>
        new
        {
            Url = dto.Properties.Url ?? string.Empty,
            Description = dto.Properties.Description ?? string.Empty,
            Protocol = dto.Properties.Protocol ?? string.Empty
        }.ToString()!;
    }

    public static Gen<BackendModel> GenerateUpdate(BackendModel original) =>
        from url in Generator.AbsoluteUri
        from description in BackendModel.GenerateDescription().OptionOf()
        select original with
        {
            Url = url,
            Description = description
        };

    public static Gen<BackendDto> GenerateOverride(BackendDto original) =>
        from url in Generator.AbsoluteUri
        from description in BackendModel.GenerateDescription().OptionOf()
        select new BackendDto
        {
            Properties = new BackendDto.BackendContract
            {
                Url = url.ToString(),
                Description = description.ValueUnsafe()
            }
        };

    public static FrozenDictionary<BackendName, BackendDto> GetDtoDictionary(IEnumerable<BackendModel> models) =>
        models.ToFrozenDictionary(model => model.Name, GetDto);

    private static BackendDto GetDto(BackendModel model) =>
        new()
        {
            Properties = new BackendDto.BackendContract
            {
                Url = model.Url.ToString(),
                Description = model.Description.ValueUnsafe(),
                Protocol = model.Protocol
            }
        };
}