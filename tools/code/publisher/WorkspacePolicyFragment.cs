using Azure.Core.Pipeline;
using common;
using LanguageExt;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace publisher;

public delegate ValueTask PutWorkspacePolicyFragments(CancellationToken cancellationToken);
public delegate Option<(PolicyFragmentName Name, WorkspaceName WorkspaceName)> TryParseWorkspacePolicyFragmentName(FileInfo file);
public delegate bool IsWorkspacePolicyFragmentNameInSourceControl(PolicyFragmentName name, WorkspaceName workspaceName);
public delegate ValueTask PutWorkspacePolicyFragment(PolicyFragmentName name, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask<Option<WorkspacePolicyFragmentDto>> FindWorkspacePolicyFragmentDto(PolicyFragmentName name, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask PutWorkspacePolicyFragmentInApim(PolicyFragmentName name, WorkspacePolicyFragmentDto dto, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspacePolicyFragments(CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspacePolicyFragment(PolicyFragmentName name, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspacePolicyFragmentFromApim(PolicyFragmentName name, WorkspaceName workspaceName, CancellationToken cancellationToken);

internal static class WorkspacePolicyFragmentModule
{
    public static void ConfigurePutWorkspacePolicyFragments(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryWorkspaceParsePolicyFragmentName(builder);
        ConfigureIsWorkspacePolicyFragmentNameInSourceControl(builder);
        ConfigurePutWorkspacePolicyFragment(builder);

        builder.Services.TryAddSingleton(GetPutWorkspacePolicyFragments);
    }

    private static PutWorkspacePolicyFragments GetPutWorkspacePolicyFragments(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseWorkspacePolicyFragmentName>();
        var isNameInSourceControl = provider.GetRequiredService<IsWorkspacePolicyFragmentNameInSourceControl>();
        var put = provider.GetRequiredService<PutWorkspacePolicyFragment>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(PutWorkspacePolicyFragments));

            logger.LogInformation("Putting workspace policy fragments...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(tag => isNameInSourceControl(tag.Name, tag.WorkspaceName))
                    .Distinct()
                    .IterParallel(put.Invoke, cancellationToken);
        };
    }

    private static void ConfigureTryWorkspaceParsePolicyFragmentName(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetTryParseWorkspacePolicyFragmentName);
    }

    private static TryParseWorkspacePolicyFragmentName GetTryParseWorkspacePolicyFragmentName(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return file => tryParseFromInformationFile(file) | tryParseFromPolicyFile(file);

        Option<(PolicyFragmentName Name, WorkspaceName WorkspaceName)> tryParseFromInformationFile(FileInfo file) =>
            from informationFile in WorkspacePolicyFragmentInformationFile.TryParse(file, serviceDirectory)
            select (informationFile.Parent.Name, informationFile.Parent.Parent.Parent.Name);

        Option<(PolicyFragmentName Name, WorkspaceName WorkspaceName)> tryParseFromPolicyFile(FileInfo file) =>
            from policyFile in WorkspacePolicyFragmentPolicyFile.TryParse(file, serviceDirectory)
            select (policyFile.Parent.Name, policyFile.Parent.Parent.Parent.Name);
    }

    private static void ConfigureIsWorkspacePolicyFragmentNameInSourceControl(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetArtifactFiles(builder);
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetIsPolicyFragmentNameInSourceControl);
    }

    private static IsWorkspacePolicyFragmentNameInSourceControl GetIsPolicyFragmentNameInSourceControl(IServiceProvider provider)
    {
        var getArtifactFiles = provider.GetRequiredService<GetArtifactFiles>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return (name, workspaceName) =>
            doesInformationFileExist(name, workspaceName)
            || doesPolicyFileExist(name, workspaceName);

        bool doesInformationFileExist(PolicyFragmentName name, WorkspaceName workspaceName)
        {
            var artifactFiles = getArtifactFiles();
            var informationFile = WorkspacePolicyFragmentInformationFile.From(name, workspaceName, serviceDirectory);

            return artifactFiles.Contains(informationFile.ToFileInfo());
        }

        bool doesPolicyFileExist(PolicyFragmentName name, WorkspaceName workspaceName)
        {
            var artifactFiles = getArtifactFiles();
            var policyFile = WorkspacePolicyFragmentPolicyFile.From(name, workspaceName, serviceDirectory);

            return artifactFiles.Contains(policyFile.ToFileInfo());
        }
    }

    private static void ConfigurePutWorkspacePolicyFragment(IHostApplicationBuilder builder)
    {
        ConfigureFindWorkspacePolicyFragmentDto(builder);
        ConfigurePutWorkspacePolicyFragmentInApim(builder);

        builder.Services.TryAddSingleton(GetPutWorkspacePolicyFragment);
    }

    private static PutWorkspacePolicyFragment GetPutWorkspacePolicyFragment(IServiceProvider provider)
    {
        var findDto = provider.GetRequiredService<FindWorkspacePolicyFragmentDto>();
        var putInApim = provider.GetRequiredService<PutWorkspacePolicyFragmentInApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutWorkspacePolicyFragment))
                                       ?.AddTag("workspace_policy_fragment.name", name)
                                       ?.AddTag("workspace.name", workspaceName);

            var dtoOption = await findDto(name, workspaceName, cancellationToken);
            await dtoOption.IterTask(async dto => await putInApim(name, dto, workspaceName, cancellationToken));
        };
    }

    private static void ConfigureFindWorkspacePolicyFragmentDto(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);
        CommonModule.ConfigureTryGetFileContents(builder);

        builder.Services.TryAddSingleton(GetFindWorkspacePolicyFragmentDto);
    }

    private static FindWorkspacePolicyFragmentDto GetFindWorkspacePolicyFragmentDto(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var tryGetFileContents = provider.GetRequiredService<TryGetFileContents>();

        return async (name, workspaceName, cancellationToken) =>
        {
            var informationFileDtoOption = await tryGetInformationFileDto(name, workspaceName, cancellationToken);
            var policyContentsOption = await tryGetPolicyContents(name, workspaceName, cancellationToken);

            return tryGetDto(informationFileDtoOption, policyContentsOption);
        };

        async ValueTask<Option<WorkspacePolicyFragmentDto>> tryGetInformationFileDto(PolicyFragmentName name, WorkspaceName workspaceName, CancellationToken cancellationToken)
        {
            var informationFile = WorkspacePolicyFragmentInformationFile.From(name, workspaceName, serviceDirectory);
            var contentsOption = await tryGetFileContents(informationFile.ToFileInfo(), cancellationToken);

            return from contents in contentsOption
                   select contents.ToObjectFromJson<WorkspacePolicyFragmentDto>();
        }

        async ValueTask<Option<BinaryData>> tryGetPolicyContents(PolicyFragmentName name, WorkspaceName workspaceName, CancellationToken cancellationToken)
        {
            var policyFile = WorkspacePolicyFragmentPolicyFile.From(name, workspaceName, serviceDirectory);

            return await tryGetFileContents(policyFile.ToFileInfo(), cancellationToken);
        }

        Option<WorkspacePolicyFragmentDto> tryGetDto(Option<WorkspacePolicyFragmentDto> informationFileDtoOption, Option<BinaryData> policyContentsOption)
        {
            if (informationFileDtoOption.IsNone && policyContentsOption.IsNone)
            {
                return Option<WorkspacePolicyFragmentDto>.None;
            }

            var dto = informationFileDtoOption.IfNone(() => new WorkspacePolicyFragmentDto { Properties = new WorkspacePolicyFragmentDto.PolicyFragmentContract() });
            policyContentsOption.Iter(contents => dto = dto with
            {
                Properties = dto.Properties with
                {
                    Format = "rawxml",
                    Value = contents.ToString()
                }
            });

            return dto;
        }
    }

    private static void ConfigurePutWorkspacePolicyFragmentInApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutWorkspacePolicyFragmentInApim);
    }

    private static PutWorkspacePolicyFragmentInApim GetPutWorkspacePolicyFragmentInApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Adding policy fragment {PolicyFragmentName} to workspace {WorkspaceName}...", name, workspaceName);

            await WorkspacePolicyFragmentUri.From(name, workspaceName, serviceUri)
                                            .PutDto(dto, pipeline, cancellationToken);
        };
    }

    public static void ConfigureDeleteWorkspacePolicyFragments(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryWorkspaceParsePolicyFragmentName(builder);
        ConfigureIsWorkspacePolicyFragmentNameInSourceControl(builder);
        ConfigureDeleteWorkspacePolicyFragment(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspacePolicyFragments);
    }

    private static DeleteWorkspacePolicyFragments GetDeleteWorkspacePolicyFragments(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseWorkspacePolicyFragmentName>();
        var isNameInSourceControl = provider.GetRequiredService<IsWorkspacePolicyFragmentNameInSourceControl>();
        var delete = provider.GetRequiredService<DeleteWorkspacePolicyFragment>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteWorkspacePolicyFragments));

            logger.LogInformation("Deleting workspace policy fragments...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(policyFragment => isNameInSourceControl(policyFragment.Name, policyFragment.WorkspaceName) is false)
                    .Distinct()
                    .IterParallel(delete.Invoke, cancellationToken);
        };
    }

    private static void ConfigureDeleteWorkspacePolicyFragment(IHostApplicationBuilder builder)
    {
        ConfigureDeleteWorkspacePolicyFragmentFromApim(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspacePolicyFragment);
    }

    private static DeleteWorkspacePolicyFragment GetDeleteWorkspacePolicyFragment(IServiceProvider provider)
    {
        var deleteFromApim = provider.GetRequiredService<DeleteWorkspacePolicyFragmentFromApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteWorkspacePolicyFragment))
                                       ?.AddTag("workspace_policy_fragment.name", name)
                                       ?.AddTag("workspace.name", workspaceName);

            await deleteFromApim(name, workspaceName, cancellationToken);
        };
    }

    private static void ConfigureDeleteWorkspacePolicyFragmentFromApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspacePolicyFragmentFromApim);
    }

    private static DeleteWorkspacePolicyFragmentFromApim GetDeleteWorkspacePolicyFragmentFromApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Removing policy fragment {PolicyFragmentName} from workspace {WorkspaceName}...", name, workspaceName);

            await WorkspacePolicyFragmentUri.From(name, workspaceName, serviceUri)
                                            .Delete(pipeline, cancellationToken);
        };
    }
}