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

internal delegate ValueTask DeleteAllGroups(ManagementServiceName serviceName, CancellationToken cancellationToken);

internal delegate ValueTask PutGroupModels(IEnumerable<GroupModel> models, ManagementServiceName serviceName, CancellationToken cancellationToken);

internal delegate ValueTask ValidateExtractedGroups(Option<FrozenSet<GroupName>> namesFilterOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

file delegate ValueTask<FrozenDictionary<GroupName, GroupDto>> GetApimGroups(ManagementServiceName serviceName, CancellationToken cancellationToken);

file delegate ValueTask<FrozenDictionary<GroupName, GroupDto>> GetFileGroups(ManagementServiceDirectory serviceDirectory, Option<CommitId> commitIdOption, CancellationToken cancellationToken);

internal delegate ValueTask WriteGroupModels(IEnumerable<GroupModel> models, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

internal delegate ValueTask ValidatePublishedGroups(IDictionary<GroupName, GroupDto> overrides, Option<CommitId> commitIdOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

file sealed class DeleteAllGroupsHandler(ILogger<DeleteAllGroups> logger, GetManagementServiceUri getServiceUri, HttpPipeline pipeline, ActivitySource activitySource)
{
    public async ValueTask Handle(ManagementServiceName serviceName, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(DeleteAllGroups));

        logger.LogInformation("Deleting all groups in {ServiceName}...", serviceName);
        var serviceUri = getServiceUri(serviceName);
        await GroupsUri.From(serviceUri).DeleteAll(pipeline, cancellationToken);
    }
}

file sealed class PutGroupModelsHandler(ILogger<PutGroupModels> logger, GetManagementServiceUri getServiceUri, HttpPipeline pipeline, ActivitySource activitySource)
{
    public async ValueTask Handle(IEnumerable<GroupModel> models, ManagementServiceName serviceName, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(PutGroupModels));

        logger.LogInformation("Putting group models in {ServiceName}...", serviceName);
        await models.IterParallel(async model =>
        {
            await Put(model, serviceName, cancellationToken);
        }, cancellationToken);
    }

    private async ValueTask Put(GroupModel model, ManagementServiceName serviceName, CancellationToken cancellationToken)
    {
        var serviceUri = getServiceUri(serviceName);
        var uri = GroupUri.From(model.Name, serviceUri);
        var dto = GetDto(model);

        await uri.PutDto(dto, pipeline, cancellationToken);
    }

    private static GroupDto GetDto(GroupModel model) =>
        new()
        {
            Properties = new GroupDto.GroupContract
            {
                DisplayName = model.DisplayName,
                Description = model.Description.ValueUnsafe()
            }
        };
}

file sealed class ValidateExtractedGroupsHandler(ILogger<ValidateExtractedGroups> logger, GetApimGroups getApimResources, GetFileGroups getFileResources, ActivitySource activitySource)
{
    public async ValueTask Handle(Option<FrozenSet<GroupName>> namesFilterOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(ValidateExtractedGroups));

        logger.LogInformation("Validating extracted groups in {ServiceName}...", serviceName);
        var apimResources = await getApimResources(serviceName, cancellationToken);
        var fileResources = await getFileResources(serviceDirectory, Prelude.None, cancellationToken);

        var expected = apimResources.WhereKey(name => ExtractorOptions.ShouldExtract(name, namesFilterOption))
                                    .MapValue(NormalizeDto);
        var actual = fileResources.MapValue(NormalizeDto);

        actual.Should().BeEquivalentTo(expected);
    }

    private static string NormalizeDto(GroupDto dto) =>
        new
        {
            DisplayName = dto.Properties.DisplayName ?? string.Empty,
            Description = dto.Properties.Description ?? string.Empty
        }.ToString()!;
}

file sealed class GetApimGroupsHandler(ILogger<GetApimGroups> logger, GetManagementServiceUri getServiceUri, HttpPipeline pipeline, ActivitySource activitySource)
{
    public async ValueTask<FrozenDictionary<GroupName, GroupDto>> Handle(ManagementServiceName serviceName, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(GetApimGroups));

        logger.LogInformation("Getting groups from {ServiceName}...", serviceName);

        var serviceUri = getServiceUri(serviceName);
        var uri = GroupsUri.From(serviceUri);

        return await uri.List(pipeline, cancellationToken)
                        .ToFrozenDictionary(cancellationToken);
    }
}

file sealed class GetFileGroupsHandler(ILogger<GetFileGroups> logger, ActivitySource activitySource)
{
    public async ValueTask<FrozenDictionary<GroupName, GroupDto>> Handle(ManagementServiceDirectory serviceDirectory, Option<CommitId> commitIdOption, CancellationToken cancellationToken) =>
        await commitIdOption.Map(commitId => GetWithCommit(serviceDirectory, commitId, cancellationToken))
                           .IfNone(() => GetWithoutCommit(serviceDirectory, cancellationToken));

    private async ValueTask<FrozenDictionary<GroupName, GroupDto>> GetWithCommit(ManagementServiceDirectory serviceDirectory, CommitId commitId, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(GetFileGroups));

        logger.LogInformation("Getting groups from {ServiceDirectory} as of commit {CommitId}...", serviceDirectory, commitId);

        return await Git.GetExistingFilesInCommit(serviceDirectory.ToDirectoryInfo(), commitId)
                        .ToAsyncEnumerable()
                        .Choose(file => GroupInformationFile.TryParse(file, serviceDirectory))
                        .Choose(async file => await TryGetCommitResource(commitId, serviceDirectory, file, cancellationToken))
                        .ToFrozenDictionary(cancellationToken);
    }

    private static async ValueTask<Option<(GroupName name, GroupDto dto)>> TryGetCommitResource(CommitId commitId, ManagementServiceDirectory serviceDirectory, GroupInformationFile file, CancellationToken cancellationToken)
    {
        var name = file.Parent.Name;
        var contentsOption = Git.TryGetFileContentsInCommit(serviceDirectory.ToDirectoryInfo(), file.ToFileInfo(), commitId);

        return await contentsOption.MapTask(async contents =>
        {
            using (contents)
            {
                var data = await BinaryData.FromStreamAsync(contents, cancellationToken);
                var dto = data.ToObjectFromJson<GroupDto>();
                return (name, dto);
            }
        });
    }

    private async ValueTask<FrozenDictionary<GroupName, GroupDto>> GetWithoutCommit(ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(GetFileGroups));

        logger.LogInformation("Getting groups from {ServiceDirectory}...", serviceDirectory);

        return await GroupModule.ListInformationFiles(serviceDirectory)
                              .ToAsyncEnumerable()
                              .SelectAwait(async file => (file.Parent.Name,
                                                          await file.ReadDto(cancellationToken)))
                              .ToFrozenDictionary(cancellationToken);
    }
}

file sealed class WriteGroupModelsHandler(ILogger<WriteGroupModels> logger, ActivitySource activitySource)
{
    public async ValueTask Handle(IEnumerable<GroupModel> models, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(WriteGroupModels));

        logger.LogInformation("Writing group models to {ServiceDirectory}...", serviceDirectory);
        await models.IterParallel(async model =>
        {
            await WriteInformationFile(model, serviceDirectory, cancellationToken);
        }, cancellationToken);
    }

    private static async ValueTask WriteInformationFile(GroupModel model, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        var informationFile = GroupInformationFile.From(model.Name, serviceDirectory);
        var dto = GetDto(model);

        await informationFile.WriteDto(dto, cancellationToken);
    }

    private static GroupDto GetDto(GroupModel model) =>
        new()
        {
            Properties = new GroupDto.GroupContract
            {
                DisplayName = model.DisplayName,
                Description = model.Description.ValueUnsafe()
            }
        };
}

file sealed class ValidatePublishedGroupsHandler(ILogger<ValidatePublishedGroups> logger, GetFileGroups getFileResources, GetApimGroups getApimResources, ActivitySource activitySource)
{
    public async ValueTask Handle(IDictionary<GroupName, GroupDto> overrides, Option<CommitId> commitIdOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(ValidatePublishedGroups));

        logger.LogInformation("Validating published groups in {ServiceDirectory}...", serviceDirectory);

        var apimResources = await getApimResources(serviceName, cancellationToken);
        var fileResources = await getFileResources(serviceDirectory, commitIdOption, cancellationToken);

        var expected = PublisherOptions.Override(fileResources, overrides)
                                       .Where(kvp => (kvp.Value.Properties.Type?.Contains("system", StringComparison.OrdinalIgnoreCase) ?? false) is false)
                                       .MapValue(NormalizeDto);
        var actual = apimResources.Where(kvp => (kvp.Value.Properties.Type?.Contains("system", StringComparison.OrdinalIgnoreCase) ?? false) is false)
                                  .MapValue(NormalizeDto);

        actual.Should().BeEquivalentTo(expected);
    }

    private static string NormalizeDto(GroupDto dto) =>
        new
        {
            DisplayName = dto.Properties.DisplayName ?? string.Empty,
            Description = dto.Properties.Description ?? string.Empty
        }.ToString()!;
}

internal static class GroupServices
{
    public static void ConfigureDeleteAllGroups(IServiceCollection services)
    {
        ManagementServices.ConfigureGetManagementServiceUri(services);

        services.TryAddSingleton<DeleteAllGroupsHandler>();
        services.TryAddSingleton<DeleteAllGroups>(provider => provider.GetRequiredService<DeleteAllGroupsHandler>().Handle);
    }

    public static void ConfigurePutGroupModels(IServiceCollection services)
    {
        ManagementServices.ConfigureGetManagementServiceUri(services);

        services.TryAddSingleton<PutGroupModelsHandler>();
        services.TryAddSingleton<PutGroupModels>(provider => provider.GetRequiredService<PutGroupModelsHandler>().Handle);
    }

    public static void ConfigureValidateExtractedGroups(IServiceCollection services)
    {
        ConfigureGetApimGroups(services);
        ConfigureGetFileGroups(services);

        services.TryAddSingleton<ValidateExtractedGroupsHandler>();
        services.TryAddSingleton<ValidateExtractedGroups>(provider => provider.GetRequiredService<ValidateExtractedGroupsHandler>().Handle);
    }

    private static void ConfigureGetApimGroups(IServiceCollection services)
    {
        ManagementServices.ConfigureGetManagementServiceUri(services);

        services.TryAddSingleton<GetApimGroupsHandler>();
        services.TryAddSingleton<GetApimGroups>(provider => provider.GetRequiredService<GetApimGroupsHandler>().Handle);
    }

    private static void ConfigureGetFileGroups(IServiceCollection services)
    {
        services.TryAddSingleton<GetFileGroupsHandler>();
        services.TryAddSingleton<GetFileGroups>(provider => provider.GetRequiredService<GetFileGroupsHandler>().Handle);
    }

    public static void ConfigureWriteGroupModels(IServiceCollection services)
    {
        services.TryAddSingleton<WriteGroupModelsHandler>();
        services.TryAddSingleton<WriteGroupModels>(provider => provider.GetRequiredService<WriteGroupModelsHandler>().Handle);
    }

    public static void ConfigureValidatePublishedGroups(IServiceCollection services)
    {
        ConfigureGetFileGroups(services);
        ConfigureGetApimGroups(services);

        services.TryAddSingleton<ValidatePublishedGroupsHandler>();
        services.TryAddSingleton<ValidatePublishedGroups>(provider => provider.GetRequiredService<ValidatePublishedGroupsHandler>().Handle);
    }
}

internal static class Group
{
    public static Gen<GroupModel> GenerateUpdate(GroupModel original) =>
        from displayName in GroupModel.GenerateDisplayName()
        from description in GroupModel.GenerateDescription().OptionOf()
        select original with
        {
            DisplayName = displayName,
            Description = description
        };

    public static Gen<GroupDto> GenerateOverride(GroupDto original) =>
        from displayName in GroupModel.GenerateDisplayName()
        from description in GroupModel.GenerateDescription().OptionOf()
        select new GroupDto
        {
            Properties = new GroupDto.GroupContract
            {
                DisplayName = displayName,
                Description = description.ValueUnsafe()
            }
        };

    public static FrozenDictionary<GroupName, GroupDto> GetDtoDictionary(IEnumerable<GroupModel> models) =>
        models.ToFrozenDictionary(model => model.Name, GetDto);

    private static GroupDto GetDto(GroupModel model) =>
        new()
        {
            Properties = new GroupDto.GroupContract
            {
                DisplayName = model.DisplayName,
                Description = model.Description.ValueUnsafe()
            }
        };
}
