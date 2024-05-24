using Azure.Core.Pipeline;
using common;
using common.tests;
using CsCheck;
using FluentAssertions;
using LanguageExt;
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

internal delegate ValueTask DeleteAllTags(ManagementServiceName serviceName, CancellationToken cancellationToken);

internal delegate ValueTask PutTagModels(IEnumerable<TagModel> models, ManagementServiceName serviceName, CancellationToken cancellationToken);

internal delegate ValueTask ValidateExtractedTags(Option<FrozenSet<TagName>> namesFilterOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

file delegate ValueTask<FrozenDictionary<TagName, TagDto>> GetApimTags(ManagementServiceName serviceName, CancellationToken cancellationToken);

file delegate ValueTask<FrozenDictionary<TagName, TagDto>> GetFileTags(ManagementServiceDirectory serviceDirectory, Option<CommitId> commitIdOption, CancellationToken cancellationToken);

internal delegate ValueTask WriteTagModels(IEnumerable<TagModel> models, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

internal delegate ValueTask ValidatePublishedTags(IDictionary<TagName, TagDto> overrides, Option<CommitId> commitIdOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

file sealed class DeleteAllTagsHandler(ILogger<DeleteAllTags> logger, GetManagementServiceUri getServiceUri, HttpPipeline pipeline, ActivitySource activitySource)
{
    public async ValueTask Handle(ManagementServiceName serviceName, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(DeleteAllTags));

        logger.LogInformation("Deleting all tags in {ServiceName}...", serviceName);
        var serviceUri = getServiceUri(serviceName);
        await TagsUri.From(serviceUri).DeleteAll(pipeline, cancellationToken);
    }
}

file sealed class PutTagModelsHandler(ILogger<PutTagModels> logger, GetManagementServiceUri getServiceUri, HttpPipeline pipeline, ActivitySource activitySource)
{
    public async ValueTask Handle(IEnumerable<TagModel> models, ManagementServiceName serviceName, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(PutTagModels));

        logger.LogInformation("Putting tag models in {ServiceName}...", serviceName);
        await models.IterParallel(async model =>
        {
            await Put(model, serviceName, cancellationToken);
        }, cancellationToken);
    }

    private async ValueTask Put(TagModel model, ManagementServiceName serviceName, CancellationToken cancellationToken)
    {
        var serviceUri = getServiceUri(serviceName);
        var uri = TagUri.From(model.Name, serviceUri);
        var dto = GetDto(model);

        await uri.PutDto(dto, pipeline, cancellationToken);
    }

    private static TagDto GetDto(TagModel model) =>
        new()
        {
            Properties = new TagDto.TagContract
            {
                DisplayName = model.DisplayName
            }
        };
}

file sealed class ValidateExtractedTagsHandler(ILogger<ValidateExtractedTags> logger, GetApimTags getApimResources, GetFileTags getFileResources, ActivitySource activitySource)
{
    public async ValueTask Handle(Option<FrozenSet<TagName>> namesFilterOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(ValidateExtractedTags));

        logger.LogInformation("Validating extracted tags in {ServiceName}...", serviceName);
        var apimResources = await getApimResources(serviceName, cancellationToken);
        var fileResources = await getFileResources(serviceDirectory, Prelude.None, cancellationToken);

        var expected = apimResources.WhereKey(name => ExtractorOptions.ShouldExtract(name, namesFilterOption))
                                    .MapValue(NormalizeDto);
        var actual = fileResources.MapValue(NormalizeDto);

        actual.Should().BeEquivalentTo(expected);
    }

    private static string NormalizeDto(TagDto dto) =>
        new
        {
            DisplayName = dto.Properties.DisplayName ?? string.Empty
        }.ToString()!;
}

file sealed class GetApimTagsHandler(ILogger<GetApimTags> logger, GetManagementServiceUri getServiceUri, HttpPipeline pipeline, ActivitySource activitySource)
{
    public async ValueTask<FrozenDictionary<TagName, TagDto>> Handle(ManagementServiceName serviceName, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(GetApimTags));

        logger.LogInformation("Getting tags from {ServiceName}...", serviceName);

        var serviceUri = getServiceUri(serviceName);
        var uri = TagsUri.From(serviceUri);

        return await uri.List(pipeline, cancellationToken)
                        .ToFrozenDictionary(cancellationToken);
    }
}

file sealed class GetFileTagsHandler(ILogger<GetFileTags> logger, ActivitySource activitySource)
{
    public async ValueTask<FrozenDictionary<TagName, TagDto>> Handle(ManagementServiceDirectory serviceDirectory, Option<CommitId> commitIdOption, CancellationToken cancellationToken) =>
        await commitIdOption.Map(commitId => GetWithCommit(serviceDirectory, commitId, cancellationToken))
                           .IfNone(() => GetWithoutCommit(serviceDirectory, cancellationToken));

    private async ValueTask<FrozenDictionary<TagName, TagDto>> GetWithCommit(ManagementServiceDirectory serviceDirectory, CommitId commitId, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(GetFileTags));

        logger.LogInformation("Getting tags from {ServiceDirectory} as of commit {CommitId}...", serviceDirectory, commitId);

        return await Git.GetExistingFilesInCommit(serviceDirectory.ToDirectoryInfo(), commitId)
                        .ToAsyncEnumerable()
                        .Choose(file => TagInformationFile.TryParse(file, serviceDirectory))
                        .Choose(async file => await TryGetCommitResource(commitId, serviceDirectory, file, cancellationToken))
                        .ToFrozenDictionary(cancellationToken);
    }

    private static async ValueTask<Option<(TagName name, TagDto dto)>> TryGetCommitResource(CommitId commitId, ManagementServiceDirectory serviceDirectory, TagInformationFile file, CancellationToken cancellationToken)
    {
        var name = file.Parent.Name;
        var contentsOption = Git.TryGetFileContentsInCommit(serviceDirectory.ToDirectoryInfo(), file.ToFileInfo(), commitId);

        return await contentsOption.MapTask(async contents =>
        {
            using (contents)
            {
                var data = await BinaryData.FromStreamAsync(contents, cancellationToken);
                var dto = data.ToObjectFromJson<TagDto>();
                return (name, dto);
            }
        });
    }

    private async ValueTask<FrozenDictionary<TagName, TagDto>> GetWithoutCommit(ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(GetFileTags));

        logger.LogInformation("Getting tags from {ServiceDirectory}...", serviceDirectory);

        return await TagModule.ListInformationFiles(serviceDirectory)
                              .ToAsyncEnumerable()
                              .SelectAwait(async file => (file.Parent.Name,
                                                          await file.ReadDto(cancellationToken)))
                              .ToFrozenDictionary(cancellationToken);
    }
}

file sealed class WriteTagModelsHandler(ILogger<WriteTagModels> logger, ActivitySource activitySource)
{
    public async ValueTask Handle(IEnumerable<TagModel> models, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(WriteTagModels));

        logger.LogInformation("Writing tag models to {ServiceDirectory}...", serviceDirectory);
        await models.IterParallel(async model =>
        {
            await WriteInformationFile(model, serviceDirectory, cancellationToken);
        }, cancellationToken);
    }

    private static async ValueTask WriteInformationFile(TagModel model, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        var informationFile = TagInformationFile.From(model.Name, serviceDirectory);
        var dto = GetDto(model);

        await informationFile.WriteDto(dto, cancellationToken);
    }

    private static TagDto GetDto(TagModel model) =>
        new()
        {
            Properties = new TagDto.TagContract
            {
                DisplayName = model.DisplayName
            }
        };
}

file sealed class ValidatePublishedTagsHandler(ILogger<ValidatePublishedTags> logger, GetFileTags getFileResources, GetApimTags getApimResources, ActivitySource activitySource)
{
    public async ValueTask Handle(IDictionary<TagName, TagDto> overrides, Option<CommitId> commitIdOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(ValidatePublishedTags));

        logger.LogInformation("Validating published tags in {ServiceDirectory}...", serviceDirectory);

        var apimResources = await getApimResources(serviceName, cancellationToken);
        var fileResources = await getFileResources(serviceDirectory, commitIdOption, cancellationToken);

        var expected = PublisherOptions.Override(fileResources, overrides)
                                       .MapValue(NormalizeDto);
        var actual = apimResources.MapValue(NormalizeDto);

        actual.Should().BeEquivalentTo(expected);
    }

    private static string NormalizeDto(TagDto dto) =>
        new
        {
            DisplayName = dto.Properties.DisplayName ?? string.Empty
        }.ToString()!;
}

internal static class TagServices
{
    public static void ConfigureDeleteAllTags(IServiceCollection services)
    {
        ManagementServices.ConfigureGetManagementServiceUri(services);

        services.TryAddSingleton<DeleteAllTagsHandler>();
        services.TryAddSingleton<DeleteAllTags>(provider => provider.GetRequiredService<DeleteAllTagsHandler>().Handle);
    }

    public static void ConfigurePutTagModels(IServiceCollection services)
    {
        ManagementServices.ConfigureGetManagementServiceUri(services);

        services.TryAddSingleton<PutTagModelsHandler>();
        services.TryAddSingleton<PutTagModels>(provider => provider.GetRequiredService<PutTagModelsHandler>().Handle);
    }

    public static void ConfigureValidateExtractedTags(IServiceCollection services)
    {
        ConfigureGetApimTags(services);
        ConfigureGetFileTags(services);

        services.TryAddSingleton<ValidateExtractedTagsHandler>();
        services.TryAddSingleton<ValidateExtractedTags>(provider => provider.GetRequiredService<ValidateExtractedTagsHandler>().Handle);
    }

    private static void ConfigureGetApimTags(IServiceCollection services)
    {
        ManagementServices.ConfigureGetManagementServiceUri(services);

        services.TryAddSingleton<GetApimTagsHandler>();
        services.TryAddSingleton<GetApimTags>(provider => provider.GetRequiredService<GetApimTagsHandler>().Handle);
    }

    private static void ConfigureGetFileTags(IServiceCollection services)
    {
        services.TryAddSingleton<GetFileTagsHandler>();
        services.TryAddSingleton<GetFileTags>(provider => provider.GetRequiredService<GetFileTagsHandler>().Handle);
    }

    public static void ConfigureWriteTagModels(IServiceCollection services)
    {
        services.TryAddSingleton<WriteTagModelsHandler>();
        services.TryAddSingleton<WriteTagModels>(provider => provider.GetRequiredService<WriteTagModelsHandler>().Handle);
    }

    public static void ConfigureValidatePublishedTags(IServiceCollection services)
    {
        ConfigureGetFileTags(services);
        ConfigureGetApimTags(services);

        services.TryAddSingleton<ValidatePublishedTagsHandler>();
        services.TryAddSingleton<ValidatePublishedTags>(provider => provider.GetRequiredService<ValidatePublishedTagsHandler>().Handle);
    }
}

internal static class Tag
{
    public static Gen<TagModel> GenerateUpdate(TagModel original) =>
        from displayName in TagModel.GenerateDisplayName()
        select original with
        {
            DisplayName = displayName
        };

    public static Gen<TagDto> GenerateOverride(TagDto original) =>
        from displayName in TagModel.GenerateDisplayName()
        select new TagDto
        {
            Properties = new TagDto.TagContract
            {
                DisplayName = displayName
            }
        };

    public static FrozenDictionary<TagName, TagDto> GetDtoDictionary(IEnumerable<TagModel> models) =>
        models.ToFrozenDictionary(model => model.Name, GetDto);

    private static TagDto GetDto(TagModel model) =>
        new()
        {
            Properties = new TagDto.TagContract
            {
                DisplayName = model.DisplayName
            }
        };
}
