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

public delegate ValueTask DeleteAllWorkspaces(ManagementServiceName serviceName, CancellationToken cancellationToken);
public delegate ValueTask PutWorkspaceModels(IEnumerable<WorkspaceModel> models, ManagementServiceName serviceName, CancellationToken cancellationToken);
public delegate ValueTask ValidateExtractedWorkspaces(Option<FrozenSet<WorkspaceName>> workspaceNamesOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);
public delegate ValueTask<FrozenDictionary<WorkspaceName, WorkspaceDto>> GetApimWorkspaces(ManagementServiceName serviceName, CancellationToken cancellationToken);
public delegate ValueTask<FrozenDictionary<WorkspaceName, WorkspaceDto>> GetFileWorkspaces(ManagementServiceDirectory serviceDirectory, Option<CommitId> commitIdOption, CancellationToken cancellationToken);
public delegate ValueTask WriteWorkspaceModels(IEnumerable<WorkspaceModel> models, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);
public delegate ValueTask ValidatePublishedWorkspaces(IDictionary<WorkspaceName, WorkspaceDto> overrides, Option<CommitId> commitIdOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

public static class WorkspaceModule
{
    public static void ConfigureDeleteAllWorkspaces(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureGetManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteAllWorkspaces);
    }

    private static DeleteAllWorkspaces GetDeleteAllWorkspaces(IServiceProvider provider)
    {
        var getServiceUri = provider.GetRequiredService<GetManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (serviceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteAllWorkspaces));

            logger.LogInformation("Deleting all workspaces in {ServiceName}...", serviceName);

            var serviceUri = getServiceUri(serviceName);

            await WorkspacesUri.From(serviceUri)
                               .DeleteAll(pipeline, cancellationToken);
        };
    }

    public static void ConfigurePutWorkspaceModels(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureGetManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceModels);
    }

    private static PutWorkspaceModels GetPutWorkspaceModels(IServiceProvider provider)
    {
        var getServiceUri = provider.GetRequiredService<GetManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (models, serviceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutWorkspaceModels));

            logger.LogInformation("Putting workspace models in {ServiceName}...", serviceName);

            await models.IterParallel(async model =>
            {
                await put(model, serviceName, cancellationToken);
            }, cancellationToken);
        };

        async ValueTask put(WorkspaceModel model, ManagementServiceName serviceName, CancellationToken cancellationToken)
        {
            var serviceUri = getServiceUri(serviceName);

            var dto = getDto(model);

            await WorkspaceUri.From(model.Name, serviceUri)
                              .PutDto(dto, pipeline, cancellationToken);
        }

        static WorkspaceDto getDto(WorkspaceModel model) =>
            new()
            {
                Properties = new WorkspaceDto.WorkspaceContract
                {
                    DisplayName = model.DisplayName,
                    Description = model.Description.ValueUnsafe()
                }
            };
    }

    public static void ConfigureValidateExtractedWorkspaces(IHostApplicationBuilder builder)
    {
        ConfigureGetApimWorkspaces(builder);
        ConfigureGetFileWorkspaces(builder);

        builder.Services.TryAddSingleton(GetValidateExtractedWorkspaces);
    }

    private static ValidateExtractedWorkspaces GetValidateExtractedWorkspaces(IServiceProvider provider)
    {
        var getApimResources = provider.GetRequiredService<GetApimWorkspaces>();
        var tryGetApimGraphQlSchema = provider.GetRequiredService<TryGetApimGraphQlSchema>();
        var getFileResources = provider.GetRequiredService<GetFileWorkspaces>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (namesFilterOption, serviceName, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ValidateExtractedWorkspaces));

            logger.LogInformation("Validating extracted workspaces in {ServiceName}...", serviceName);

            var apimResources = await getApimResources(serviceName, cancellationToken);
            var fileResources = await getFileResources(serviceDirectory, Prelude.None, cancellationToken);

            var expected = apimResources.WhereKey(name => ExtractorOptions.ShouldExtract(name, namesFilterOption))
                                        .MapValue(normalizeDto)
                                        .ToFrozenDictionary();

            var actual = fileResources.MapValue(normalizeDto)
                                      .ToFrozenDictionary();

            actual.Should().BeEquivalentTo(expected);
        };

        static string normalizeDto(WorkspaceDto dto) =>
            new
            {
                DisplayName = dto.Properties.DisplayName ?? string.Empty,
                Description = dto.Properties.Description ?? string.Empty
            }.ToString()!;
    }

    public static void ConfigureGetApimWorkspaces(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureGetManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetGetApimWorkspaces);
    }

    private static GetApimWorkspaces GetGetApimWorkspaces(IServiceProvider provider)
    {
        var getServiceUri = provider.GetRequiredService<GetManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (serviceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(GetApimWorkspaces));

            logger.LogInformation("Getting workspaces from {ServiceName}...", serviceName);

            var serviceUri = getServiceUri(serviceName);

            return await WorkspacesUri.From(serviceUri)
                                      .List(pipeline, cancellationToken)
                                      .ToFrozenDictionary(cancellationToken);
        };
    }

    public static void ConfigureGetFileWorkspaces(IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(GetGetFileWorkspaces);
    }

    private static GetFileWorkspaces GetGetFileWorkspaces(IServiceProvider provider)
    {
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (serviceDirectory, commitIdOption, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(GetFileWorkspaces));

            return await commitIdOption.Map(commitId => getWithCommit(serviceDirectory, commitId, cancellationToken))
                                       .IfNone(() => getWithoutCommit(serviceDirectory, cancellationToken));
        };

        async ValueTask<FrozenDictionary<WorkspaceName, WorkspaceDto>> getWithCommit(ManagementServiceDirectory serviceDirectory, CommitId commitId, CancellationToken cancellationToken)
        {
            using var _ = activitySource.StartActivity(nameof(GetFileWorkspaces));

            logger.LogInformation("Getting workspaces from {ServiceDirectory} as of commit {CommitId}...", serviceDirectory, commitId);

            return await Git.GetExistingFilesInCommit(serviceDirectory.ToDirectoryInfo(), commitId)
                            .ToAsyncEnumerable()
                            .Choose(file => WorkspaceInformationFile.TryParse(file, serviceDirectory))
                            .Choose(async file => await tryGetCommitResource(commitId, serviceDirectory, file, cancellationToken))
                            .ToFrozenDictionary(cancellationToken);
        }

        static async ValueTask<Option<(WorkspaceName name, WorkspaceDto dto)>> tryGetCommitResource(CommitId commitId, ManagementServiceDirectory serviceDirectory, WorkspaceInformationFile file, CancellationToken cancellationToken)
        {
            var name = file.Parent.Name;
            var contentsOption = Git.TryGetFileContentsInCommit(serviceDirectory.ToDirectoryInfo(), file.ToFileInfo(), commitId);

            return await contentsOption.MapTask(async contents =>
            {
                using (contents)
                {
                    var data = await BinaryData.FromStreamAsync(contents, cancellationToken);
                    var dto = data.ToObjectFromJson<WorkspaceDto>();
                    return (name, dto);
                }
            });
        }

        async ValueTask<FrozenDictionary<WorkspaceName, WorkspaceDto>> getWithoutCommit(ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
        {
            logger.LogInformation("Getting workspaces from {ServiceDirectory}...", serviceDirectory);

            return await common.WorkspaceModule.ListInformationFiles(serviceDirectory)
                                               .ToAsyncEnumerable()
                                               .SelectAwait(async file => (file.Parent.Name,
                                                                           await file.ReadDto(cancellationToken)))
                                               .ToFrozenDictionary(cancellationToken);
        }
    }

    public static void ConfigureWriteWorkspaceModels(IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(GetWriteWorkspaceModels);
    }

    private static WriteWorkspaceModels GetWriteWorkspaceModels(IServiceProvider provider)
    {
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (models, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(WriteWorkspaceModels));

            logger.LogInformation("Writing workspace models to {ServiceDirectory}...", serviceDirectory);

            await models.IterParallel(async model =>
            {
                await writeInformationFile(model, serviceDirectory, cancellationToken);
            }, cancellationToken);
        };

        static async ValueTask writeInformationFile(WorkspaceModel model, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
        {
            var informationFile = WorkspaceInformationFile.From(model.Name, serviceDirectory);
            var dto = getDto(model);

            await informationFile.WriteDto(dto, cancellationToken);
        }

        static WorkspaceDto getDto(WorkspaceModel model) =>
            new()
            {
                Properties = new WorkspaceDto.WorkspaceContract
                {
                    DisplayName = model.DisplayName,
                    Description = model.Description.ValueUnsafe()
                }
            };
    }

    public static void ConfigureValidatePublishedWorkspaces(IHostApplicationBuilder builder)
    {
        ConfigureGetFileWorkspaces(builder);
        ConfigureGetApimWorkspaces(builder);

        builder.Services.TryAddSingleton(GetValidatePublishedWorkspaces);
    }

    private static ValidatePublishedWorkspaces GetValidatePublishedWorkspaces(IServiceProvider provider)
    {
        var getFileResources = provider.GetRequiredService<GetFileWorkspaces>();
        var getApimResources = provider.GetRequiredService<GetApimWorkspaces>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (overrides, commitIdOption, serviceName, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ValidatePublishedWorkspaces));

            logger.LogInformation("Validating published workspaces in {ServiceDirectory}...", serviceDirectory);

            var apimResources = await getApimResources(serviceName, cancellationToken);
            var fileResources = await getFileResources(serviceDirectory, commitIdOption, cancellationToken);

            var expected = PublisherOptions.Override(fileResources, overrides)
                                           .MapValue(normalizeDto)
                                           .ToFrozenDictionary();

            var actual = apimResources.MapValue(normalizeDto)
                                      .ToFrozenDictionary();

            actual.Should().BeEquivalentTo(expected);
        };

        static string normalizeDto(WorkspaceDto dto) =>
            new
            {
                DisplayName = dto.Properties.DisplayName ?? string.Empty,
                Description = dto.Properties.Description ?? string.Empty
            }.ToString()!;
    }

    public static Gen<WorkspaceModel> GenerateUpdate(WorkspaceModel original) =>
        from displayName in WorkspaceModel.GenerateDisplayName()
        from description in WorkspaceModel.GenerateDescription().OptionOf()
        select original with
        {
            DisplayName = displayName,
            Description = description
        };

    public static Gen<WorkspaceDto> GenerateOverride(WorkspaceDto original) =>
        from displayName in WorkspaceModel.GenerateDisplayName()
        from description in WorkspaceModel.GenerateDescription().OptionOf()
        select new WorkspaceDto
        {
            Properties = new WorkspaceDto.WorkspaceContract
            {
                DisplayName = displayName,
                Description = description.ValueUnsafe()
            }
        };

    public static FrozenDictionary<WorkspaceName, WorkspaceDto> GetDtoDictionary(IEnumerable<WorkspaceModel> models) =>
        models.ToFrozenDictionary(model => model.Name, GetDto);

    private static WorkspaceDto GetDto(WorkspaceModel model) =>
        new()
        {
            Properties = new WorkspaceDto.WorkspaceContract
            {
                DisplayName = model.DisplayName,
                Description = model.Description.ValueUnsafe()
            }
        };
}