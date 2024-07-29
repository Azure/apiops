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

public delegate ValueTask DeleteAllGroups(ManagementServiceName serviceName, CancellationToken cancellationToken);
public delegate ValueTask PutGroupModels(IEnumerable<GroupModel> models, ManagementServiceName serviceName, CancellationToken cancellationToken);
public delegate ValueTask ValidateExtractedGroups(Option<FrozenSet<GroupName>> groupNamesOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);
public delegate ValueTask<FrozenDictionary<GroupName, GroupDto>> GetApimGroups(ManagementServiceName serviceName, CancellationToken cancellationToken);
public delegate ValueTask<FrozenDictionary<GroupName, GroupDto>> GetFileGroups(ManagementServiceDirectory serviceDirectory, Option<CommitId> commitIdOption, CancellationToken cancellationToken);
public delegate ValueTask WriteGroupModels(IEnumerable<GroupModel> models, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);
public delegate ValueTask ValidatePublishedGroups(IDictionary<GroupName, GroupDto> overrides, Option<CommitId> commitIdOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

public static class GroupModule
{
    public static void ConfigureDeleteAllGroups(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureGetManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteAllGroups);
    }

    private static DeleteAllGroups GetDeleteAllGroups(IServiceProvider provider)
    {
        var getServiceUri = provider.GetRequiredService<GetManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (serviceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteAllGroups));

            logger.LogInformation("Deleting all groups in {ServiceName}...", serviceName);

            var serviceUri = getServiceUri(serviceName);

            await GroupsUri.From(serviceUri)
                           .DeleteAll(pipeline, cancellationToken);
        };
    }

    public static void ConfigurePutGroupModels(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureGetManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutGroupModels);
    }

    private static PutGroupModels GetPutGroupModels(IServiceProvider provider)
    {
        var getServiceUri = provider.GetRequiredService<GetManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (models, serviceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutGroupModels));

            logger.LogInformation("Putting group models in {ServiceName}...", serviceName);

            await models.IterParallel(async model =>
            {
                await put(model, serviceName, cancellationToken);
            }, cancellationToken);
        };

        async ValueTask put(GroupModel model, ManagementServiceName serviceName, CancellationToken cancellationToken)
        {
            var serviceUri = getServiceUri(serviceName);

            var dto = getDto(model);

            await GroupUri.From(model.Name, serviceUri)
                          .PutDto(dto, pipeline, cancellationToken);
        }

        static GroupDto getDto(GroupModel model) =>
            new()
            {
                Properties = new GroupDto.GroupContract
                {
                    DisplayName = model.DisplayName,
                    Description = model.Description.ValueUnsafe()
                }
            };
    }

    public static void ConfigureValidateExtractedGroups(IHostApplicationBuilder builder)
    {
        ConfigureGetApimGroups(builder);
        ConfigureGetFileGroups(builder);

        builder.Services.TryAddSingleton(GetValidateExtractedGroups);
    }

    private static ValidateExtractedGroups GetValidateExtractedGroups(IServiceProvider provider)
    {
        var getApimResources = provider.GetRequiredService<GetApimGroups>();
        var tryGetApimGraphQlSchema = provider.GetRequiredService<TryGetApimGraphQlSchema>();
        var getFileResources = provider.GetRequiredService<GetFileGroups>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (namesFilterOption, serviceName, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ValidateExtractedGroups));

            logger.LogInformation("Validating extracted groups in {ServiceName}...", serviceName);

            var apimResources = await getApimResources(serviceName, cancellationToken);
            var fileResources = await getFileResources(serviceDirectory, Prelude.None, cancellationToken);

            var expected = apimResources.WhereKey(name => ExtractorOptions.ShouldExtract(name, namesFilterOption))
                                        .MapValue(normalizeDto)
                                        .ToFrozenDictionary();

            var actual = fileResources.MapValue(normalizeDto)
                                      .ToFrozenDictionary();

            actual.Should().BeEquivalentTo(expected);
        };

        static string normalizeDto(GroupDto dto) =>
            new
            {
                DisplayName = dto.Properties.DisplayName ?? string.Empty,
                Description = dto.Properties.Description ?? string.Empty
            }.ToString()!;
    }

    public static void ConfigureGetApimGroups(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureGetManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetGetApimGroups);
    }

    private static GetApimGroups GetGetApimGroups(IServiceProvider provider)
    {
        var getServiceUri = provider.GetRequiredService<GetManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (serviceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(GetApimGroups));

            logger.LogInformation("Getting groups from {ServiceName}...", serviceName);

            var serviceUri = getServiceUri(serviceName);

            return await GroupsUri.From(serviceUri)
                                  .List(pipeline, cancellationToken)
                                  .ToFrozenDictionary(cancellationToken);
        };
    }

    public static void ConfigureGetFileGroups(IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(GetGetFileGroups);
    }

    private static GetFileGroups GetGetFileGroups(IServiceProvider provider)
    {
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (serviceDirectory, commitIdOption, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(GetFileGroups));

            return await commitIdOption.Map(commitId => getWithCommit(serviceDirectory, commitId, cancellationToken))
                                       .IfNone(() => getWithoutCommit(serviceDirectory, cancellationToken));
        };

        async ValueTask<FrozenDictionary<GroupName, GroupDto>> getWithCommit(ManagementServiceDirectory serviceDirectory, CommitId commitId, CancellationToken cancellationToken)
        {
            using var _ = activitySource.StartActivity(nameof(GetFileGroups));

            logger.LogInformation("Getting groups from {ServiceDirectory} as of commit {CommitId}...", serviceDirectory, commitId);

            return await Git.GetExistingFilesInCommit(serviceDirectory.ToDirectoryInfo(), commitId)
                            .ToAsyncEnumerable()
                            .Choose(file => GroupInformationFile.TryParse(file, serviceDirectory))
                            .Choose(async file => await tryGetCommitResource(commitId, serviceDirectory, file, cancellationToken))
                            .ToFrozenDictionary(cancellationToken);
        }

        static async ValueTask<Option<(GroupName name, GroupDto dto)>> tryGetCommitResource(CommitId commitId, ManagementServiceDirectory serviceDirectory, GroupInformationFile file, CancellationToken cancellationToken)
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

        async ValueTask<FrozenDictionary<GroupName, GroupDto>> getWithoutCommit(ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
        {
            logger.LogInformation("Getting groups from {ServiceDirectory}...", serviceDirectory);

            return await common.GroupModule.ListInformationFiles(serviceDirectory)
                                           .ToAsyncEnumerable()
                                           .SelectAwait(async file => (file.Parent.Name,
                                                                       await file.ReadDto(cancellationToken)))
                                           .ToFrozenDictionary(cancellationToken);
        }
    }

    public static void ConfigureWriteGroupModels(IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(GetWriteGroupModels);
    }

    private static WriteGroupModels GetWriteGroupModels(IServiceProvider provider)
    {
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (models, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(WriteGroupModels));

            logger.LogInformation("Writing group models to {ServiceDirectory}...", serviceDirectory);

            await models.IterParallel(async model =>
            {
                await writeInformationFile(model, serviceDirectory, cancellationToken);
            }, cancellationToken);
        };

        static async ValueTask writeInformationFile(GroupModel model, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
        {
            var informationFile = GroupInformationFile.From(model.Name, serviceDirectory);
            var dto = getDto(model);

            await informationFile.WriteDto(dto, cancellationToken);
        }

        static GroupDto getDto(GroupModel model) =>
            new()
            {
                Properties = new GroupDto.GroupContract
                {
                    DisplayName = model.DisplayName,
                    Description = model.Description.ValueUnsafe()
                }
            };
    }

    public static void ConfigureValidatePublishedGroups(IHostApplicationBuilder builder)
    {
        ConfigureGetFileGroups(builder);
        ConfigureGetApimGroups(builder);

        builder.Services.TryAddSingleton(GetValidatePublishedGroups);
    }

    private static ValidatePublishedGroups GetValidatePublishedGroups(IServiceProvider provider)
    {
        var getFileResources = provider.GetRequiredService<GetFileGroups>();
        var getApimResources = provider.GetRequiredService<GetApimGroups>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (overrides, commitIdOption, serviceName, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ValidatePublishedGroups));

            logger.LogInformation("Validating published groups in {ServiceDirectory}...", serviceDirectory);

            var apimResources = await getApimResources(serviceName, cancellationToken);
            var fileResources = await getFileResources(serviceDirectory, commitIdOption, cancellationToken);

            var expected = PublisherOptions.Override(fileResources, overrides)
                                           .WhereValue(dto => (dto.Properties.Type?.Contains("system", StringComparison.OrdinalIgnoreCase) ?? false) is false)
                                           .MapValue(normalizeDto)
                                           .ToFrozenDictionary();

            var actual = apimResources.WhereValue(dto => (dto.Properties.Type?.Contains("system", StringComparison.OrdinalIgnoreCase) ?? false) is false)
                                      .MapValue(normalizeDto)
                                      .ToFrozenDictionary();

            actual.Should().BeEquivalentTo(expected);
        };

        static string normalizeDto(GroupDto dto) =>
            new
            {
                DisplayName = dto.Properties.DisplayName ?? string.Empty,
                Description = dto.Properties.Description ?? string.Empty
            }.ToString()!;
    }

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