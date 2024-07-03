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

internal delegate ValueTask DeleteAllBackends(ManagementServiceName serviceName, CancellationToken cancellationToken);

internal delegate ValueTask PutBackendModels(IEnumerable<BackendModel> models, ManagementServiceName serviceName, CancellationToken cancellationToken);

internal delegate ValueTask ValidateExtractedBackends(Option<FrozenSet<BackendName>> namesFilterOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

file delegate ValueTask<FrozenDictionary<BackendName, BackendDto>> GetApimBackends(ManagementServiceName serviceName, CancellationToken cancellationToken);

file delegate ValueTask<FrozenDictionary<BackendName, BackendDto>> GetFileBackends(ManagementServiceDirectory serviceDirectory, Option<CommitId> commitIdOption, CancellationToken cancellationToken);

internal delegate ValueTask WriteBackendModels(IEnumerable<BackendModel> models, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

internal delegate ValueTask ValidatePublishedBackends(IDictionary<BackendName, BackendDto> overrides, Option<CommitId> commitIdOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

file sealed class DeleteAllBackendsHandler(ILogger<DeleteAllBackends> logger, GetManagementServiceUri getServiceUri, HttpPipeline pipeline, ActivitySource activitySource)
{
    public async ValueTask Handle(ManagementServiceName serviceName, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(DeleteAllBackends));

        logger.LogInformation("Deleting all backends in {ServiceName}...", serviceName);
        var serviceUri = getServiceUri(serviceName);
        await BackendsUri.From(serviceUri).DeleteAll(pipeline, cancellationToken);
    }
}

file sealed class PutBackendModelsHandler(ILogger<PutBackendModels> logger, GetManagementServiceUri getServiceUri, HttpPipeline pipeline, ActivitySource activitySource)
{
    public async ValueTask Handle(IEnumerable<BackendModel> models, ManagementServiceName serviceName, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(PutBackendModels));

        logger.LogInformation("Putting backend models in {ServiceName}...", serviceName);
        await models.IterParallel(async model =>
        {
            await Put(model, serviceName, cancellationToken);
        }, cancellationToken);
    }

    private async ValueTask Put(BackendModel model, ManagementServiceName serviceName, CancellationToken cancellationToken)
    {
        var serviceUri = getServiceUri(serviceName);
        var uri = BackendUri.From(model.Name, serviceUri);
        var dto = GetDto(model);

        await uri.PutDto(dto, pipeline, cancellationToken);
    }

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

file sealed class ValidateExtractedBackendsHandler(ILogger<ValidateExtractedBackends> logger, GetApimBackends getApimResources, GetFileBackends getFileResources, ActivitySource activitySource)
{
    public async ValueTask Handle(Option<FrozenSet<BackendName>> namesFilterOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(ValidateExtractedBackends));

        logger.LogInformation("Validating extracted backends in {ServiceName}...", serviceName);
        var apimResources = await getApimResources(serviceName, cancellationToken);
        var fileResources = await getFileResources(serviceDirectory, Prelude.None, cancellationToken);

        var expected = apimResources.WhereKey(name => ExtractorOptions.ShouldExtract(name, namesFilterOption))
                                    .MapValue(NormalizeDto);
        var actual = fileResources.MapValue(NormalizeDto);

        actual.Should().BeEquivalentTo(expected);
    }

    private static string NormalizeDto(BackendDto dto) =>
        new
        {
            Url = dto.Properties.Url ?? string.Empty,
            Description = dto.Properties.Description ?? string.Empty,
            Protocol = dto.Properties.Protocol ?? string.Empty
        }.ToString()!;
}

file sealed class GetApimBackendsHandler(ILogger<GetApimBackends> logger, GetManagementServiceUri getServiceUri, HttpPipeline pipeline, ActivitySource activitySource)
{
    public async ValueTask<FrozenDictionary<BackendName, BackendDto>> Handle(ManagementServiceName serviceName, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(GetApimBackends));

        logger.LogInformation("Getting backends from {ServiceName}...", serviceName);

        var serviceUri = getServiceUri(serviceName);
        var uri = BackendsUri.From(serviceUri);

        return await uri.List(pipeline, cancellationToken)
                        .ToFrozenDictionary(cancellationToken);
    }
}

file sealed class GetFileBackendsHandler(ILogger<GetFileBackends> logger, ActivitySource activitySource)
{
    public async ValueTask<FrozenDictionary<BackendName, BackendDto>> Handle(ManagementServiceDirectory serviceDirectory, Option<CommitId> commitIdOption, CancellationToken cancellationToken) =>
        await commitIdOption.Map(commitId => GetWithCommit(serviceDirectory, commitId, cancellationToken))
                           .IfNone(() => GetWithoutCommit(serviceDirectory, cancellationToken));

    private async ValueTask<FrozenDictionary<BackendName, BackendDto>> GetWithCommit(ManagementServiceDirectory serviceDirectory, CommitId commitId, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(GetFileBackends));

        logger.LogInformation("Getting backends from {ServiceDirectory} as of commit {CommitId}...", serviceDirectory, commitId);

        return await Git.GetExistingFilesInCommit(serviceDirectory.ToDirectoryInfo(), commitId)
                        .ToAsyncEnumerable()
                        .Choose(file => BackendInformationFile.TryParse(file, serviceDirectory))
                        .Choose(async file => await TryGetCommitResource(commitId, serviceDirectory, file, cancellationToken))
                        .ToFrozenDictionary(cancellationToken);
    }

    private static async ValueTask<Option<(BackendName name, BackendDto dto)>> TryGetCommitResource(CommitId commitId, ManagementServiceDirectory serviceDirectory, BackendInformationFile file, CancellationToken cancellationToken)
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

    private async ValueTask<FrozenDictionary<BackendName, BackendDto>> GetWithoutCommit(ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(GetFileBackends));

        logger.LogInformation("Getting backends from {ServiceDirectory}...", serviceDirectory);

        return await BackendModule.ListInformationFiles(serviceDirectory)
                              .ToAsyncEnumerable()
                              .SelectAwait(async file => (file.Parent.Name,
                                                          await file.ReadDto(cancellationToken)))
                              .ToFrozenDictionary(cancellationToken);
    }
}

file sealed class WriteBackendModelsHandler(ILogger<WriteBackendModels> logger, ActivitySource activitySource)
{
    public async ValueTask Handle(IEnumerable<BackendModel> models, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(WriteBackendModels));

        logger.LogInformation("Writing backend models to {ServiceDirectory}...", serviceDirectory);
        await models.IterParallel(async model =>
        {
            await WriteInformationFile(model, serviceDirectory, cancellationToken);
        }, cancellationToken);
    }

    private static async ValueTask WriteInformationFile(BackendModel model, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        var informationFile = BackendInformationFile.From(model.Name, serviceDirectory);
        var dto = GetDto(model);

        await informationFile.WriteDto(dto, cancellationToken);
    }

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

file sealed class ValidatePublishedBackendsHandler(ILogger<ValidatePublishedBackends> logger, GetFileBackends getFileResources, GetApimBackends getApimResources, ActivitySource activitySource)
{
    public async ValueTask Handle(IDictionary<BackendName, BackendDto> overrides, Option<CommitId> commitIdOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(ValidatePublishedBackends));

        logger.LogInformation("Validating published backends in {ServiceDirectory}...", serviceDirectory);

        var apimResources = await getApimResources(serviceName, cancellationToken);
        var fileResources = await getFileResources(serviceDirectory, commitIdOption, cancellationToken);

        var expected = PublisherOptions.Override(fileResources, overrides)
                                       .MapValue(NormalizeDto);
        var actual = apimResources.MapValue(NormalizeDto);

        actual.Should().BeEquivalentTo(expected);
    }

    private static string NormalizeDto(BackendDto dto) =>
        new
        {
            Url = dto.Properties.Url ?? string.Empty,
            Description = dto.Properties.Description ?? string.Empty,
            Protocol = dto.Properties.Protocol ?? string.Empty
        }.ToString()!;
}

internal static class BackendServices
{
    public static void ConfigureDeleteAllBackends(IServiceCollection services)
    {
        ManagementServices.ConfigureGetManagementServiceUri(services);

        services.TryAddSingleton<DeleteAllBackendsHandler>();
        services.TryAddSingleton<DeleteAllBackends>(provider => provider.GetRequiredService<DeleteAllBackendsHandler>().Handle);
    }

    public static void ConfigurePutBackendModels(IServiceCollection services)
    {
        ManagementServices.ConfigureGetManagementServiceUri(services);

        services.TryAddSingleton<PutBackendModelsHandler>();
        services.TryAddSingleton<PutBackendModels>(provider => provider.GetRequiredService<PutBackendModelsHandler>().Handle);
    }

    public static void ConfigureValidateExtractedBackends(IServiceCollection services)
    {
        ConfigureGetApimBackends(services);
        ConfigureGetFileBackends(services);

        services.TryAddSingleton<ValidateExtractedBackendsHandler>();
        services.TryAddSingleton<ValidateExtractedBackends>(provider => provider.GetRequiredService<ValidateExtractedBackendsHandler>().Handle);
    }

    private static void ConfigureGetApimBackends(IServiceCollection services)
    {
        ManagementServices.ConfigureGetManagementServiceUri(services);

        services.TryAddSingleton<GetApimBackendsHandler>();
        services.TryAddSingleton<GetApimBackends>(provider => provider.GetRequiredService<GetApimBackendsHandler>().Handle);
    }

    private static void ConfigureGetFileBackends(IServiceCollection services)
    {
        services.TryAddSingleton<GetFileBackendsHandler>();
        services.TryAddSingleton<GetFileBackends>(provider => provider.GetRequiredService<GetFileBackendsHandler>().Handle);
    }

    public static void ConfigureWriteBackendModels(IServiceCollection services)
    {
        services.TryAddSingleton<WriteBackendModelsHandler>();
        services.TryAddSingleton<WriteBackendModels>(provider => provider.GetRequiredService<WriteBackendModelsHandler>().Handle);
    }

    public static void ConfigureValidatePublishedBackends(IServiceCollection services)
    {
        ConfigureGetFileBackends(services);
        ConfigureGetApimBackends(services);

        services.TryAddSingleton<ValidatePublishedBackendsHandler>();
        services.TryAddSingleton<ValidatePublishedBackends>(provider => provider.GetRequiredService<ValidatePublishedBackendsHandler>().Handle);
    }
}

internal static class Backend
{
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
