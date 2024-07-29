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

public delegate ValueTask PutPolicyFragments(CancellationToken cancellationToken);
public delegate Option<PolicyFragmentName> TryParsePolicyFragmentName(FileInfo file);
public delegate bool IsPolicyFragmentNameInSourceControl(PolicyFragmentName name);
public delegate ValueTask PutPolicyFragment(PolicyFragmentName name, CancellationToken cancellationToken);
public delegate ValueTask<Option<PolicyFragmentDto>> FindPolicyFragmentDto(PolicyFragmentName name, CancellationToken cancellationToken);
public delegate ValueTask PutPolicyFragmentInApim(PolicyFragmentName name, PolicyFragmentDto dto, CancellationToken cancellationToken);
public delegate ValueTask DeletePolicyFragments(CancellationToken cancellationToken);
public delegate ValueTask DeletePolicyFragment(PolicyFragmentName name, CancellationToken cancellationToken);
public delegate ValueTask DeletePolicyFragmentFromApim(PolicyFragmentName name, CancellationToken cancellationToken);

internal static class PolicyFragmentModule
{
    public static void ConfigurePutPolicyFragments(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParsePolicyFragmentName(builder);
        ConfigureIsPolicyFragmentNameInSourceControl(builder);
        ConfigurePutPolicyFragment(builder);

        builder.Services.TryAddSingleton(GetPutPolicyFragments);
    }

    private static PutPolicyFragments GetPutPolicyFragments(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParsePolicyFragmentName>();
        var isNameInSourceControl = provider.GetRequiredService<IsPolicyFragmentNameInSourceControl>();
        var put = provider.GetRequiredService<PutPolicyFragment>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(PutPolicyFragments));

            logger.LogInformation("Putting policy fragments...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(isNameInSourceControl.Invoke)
                    .Distinct()
                    .IterParallel(put.Invoke, cancellationToken);
        };
    }

    private static void ConfigureTryParsePolicyFragmentName(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetTryParsePolicyFragmentName);
    }

    private static TryParsePolicyFragmentName GetTryParsePolicyFragmentName(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return file => tryParseFromInformationFile(file) | tryParseFromPolicyFile(file);

        Option<PolicyFragmentName> tryParseFromInformationFile(FileInfo file) =>
            from informationFile in PolicyFragmentInformationFile.TryParse(file, serviceDirectory)
            select informationFile.Parent.Name;

        Option<PolicyFragmentName> tryParseFromPolicyFile(FileInfo file) =>
            from policyFile in PolicyFragmentPolicyFile.TryParse(file, serviceDirectory)
            select policyFile.Parent.Name;
    }

    private static void ConfigureIsPolicyFragmentNameInSourceControl(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetArtifactFiles(builder);
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetIsPolicyFragmentNameInSourceControl);
    }

    private static IsPolicyFragmentNameInSourceControl GetIsPolicyFragmentNameInSourceControl(IServiceProvider provider)
    {
        var getArtifactFiles = provider.GetRequiredService<GetArtifactFiles>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return name =>
            doesInformationFileExist(name)
            || doesPolicyFileExist(name);

        bool doesInformationFileExist(PolicyFragmentName name)
        {
            var artifactFiles = getArtifactFiles();
            var informationFile = PolicyFragmentInformationFile.From(name, serviceDirectory);

            return artifactFiles.Contains(informationFile.ToFileInfo());
        }

        bool doesPolicyFileExist(PolicyFragmentName name)
        {
            var artifactFiles = getArtifactFiles();
            var policyFile = PolicyFragmentPolicyFile.From(name, serviceDirectory);

            return artifactFiles.Contains(policyFile.ToFileInfo());
        }
    }

    private static void ConfigurePutPolicyFragment(IHostApplicationBuilder builder)
    {
        ConfigureFindPolicyFragmentDto(builder);
        ConfigurePutPolicyFragmentInApim(builder);

        builder.Services.TryAddSingleton(GetPutPolicyFragment);
    }

    private static PutPolicyFragment GetPutPolicyFragment(IServiceProvider provider)
    {
        var findDto = provider.GetRequiredService<FindPolicyFragmentDto>();
        var putInApim = provider.GetRequiredService<PutPolicyFragmentInApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutPolicyFragment))
                                       ?.AddTag("policy_fragment.name", name);

            var dtoOption = await findDto(name, cancellationToken);
            await dtoOption.IterTask(async dto => await putInApim(name, dto, cancellationToken));
        };
    }

    private static void ConfigureFindPolicyFragmentDto(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);
        CommonModule.ConfigureTryGetFileContents(builder);
        OverrideDtoModule.ConfigureOverrideDtoFactory(builder);

        builder.Services.TryAddSingleton(GetFindPolicyFragmentDto);
    }

    private static FindPolicyFragmentDto GetFindPolicyFragmentDto(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var tryGetFileContents = provider.GetRequiredService<TryGetFileContents>();
        var overrideFactory = provider.GetRequiredService<OverrideDtoFactory>();

        var overrideDto = overrideFactory.Create<PolicyFragmentName, PolicyFragmentDto>();

        return async (name, cancellationToken) =>
        {
            var informationFileDtoOption = await tryGetInformationFileDto(name, cancellationToken);
            var policyContentsOption = await tryGetPolicyContents(name, cancellationToken);

            return tryGetDto(name, informationFileDtoOption, policyContentsOption);
        };

        async ValueTask<Option<PolicyFragmentDto>> tryGetInformationFileDto(PolicyFragmentName name, CancellationToken cancellationToken)
        {
            var informationFile = PolicyFragmentInformationFile.From(name, serviceDirectory);
            var contentsOption = await tryGetFileContents(informationFile.ToFileInfo(), cancellationToken);

            return from contents in contentsOption
                   select contents.ToObjectFromJson<PolicyFragmentDto>();
        }

        async ValueTask<Option<BinaryData>> tryGetPolicyContents(PolicyFragmentName name, CancellationToken cancellationToken)
        {
            var policyFile = PolicyFragmentPolicyFile.From(name, serviceDirectory);

            return await tryGetFileContents(policyFile.ToFileInfo(), cancellationToken);
        }

        Option<PolicyFragmentDto> tryGetDto(PolicyFragmentName name, Option<PolicyFragmentDto> informationFileDtoOption, Option<BinaryData> policyContentsOption)
        {
            if (informationFileDtoOption.IsNone && policyContentsOption.IsNone)
            {
                return Option<PolicyFragmentDto>.None;
            }

            var dto = informationFileDtoOption.IfNone(() => new PolicyFragmentDto { Properties = new PolicyFragmentDto.PolicyFragmentContract() });
            policyContentsOption.Iter(contents => dto = dto with
            {
                Properties = dto.Properties with
                {
                    Format = "rawxml",
                    Value = contents.ToString()
                }
            });

            return overrideDto(name, dto);
        }
    }

    private static void ConfigurePutPolicyFragmentInApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutPolicyFragmentInApim);
    }

    private static PutPolicyFragmentInApim GetPutPolicyFragmentInApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, cancellationToken) =>
        {
            logger.LogInformation("Putting policy fragment {PolicyFragmentName}...", name);

            await PolicyFragmentUri.From(name, serviceUri)
                                   .PutDto(dto, pipeline, cancellationToken);
        };
    }

    public static void ConfigureDeletePolicyFragments(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParsePolicyFragmentName(builder);
        ConfigureIsPolicyFragmentNameInSourceControl(builder);
        ConfigureDeletePolicyFragment(builder);

        builder.Services.TryAddSingleton(GetDeletePolicyFragments);
    }

    private static DeletePolicyFragments GetDeletePolicyFragments(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParsePolicyFragmentName>();
        var isNameInSourceControl = provider.GetRequiredService<IsPolicyFragmentNameInSourceControl>();
        var delete = provider.GetRequiredService<DeletePolicyFragment>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(DeletePolicyFragments));

            logger.LogInformation("Deleting policy fragments...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(name => isNameInSourceControl(name) is false)
                    .Distinct()
                    .IterParallel(delete.Invoke, cancellationToken);
        };
    }

    private static void ConfigureDeletePolicyFragment(IHostApplicationBuilder builder)
    {
        ConfigureDeletePolicyFragmentFromApim(builder);

        builder.Services.TryAddSingleton(GetDeletePolicyFragment);
    }

    private static DeletePolicyFragment GetDeletePolicyFragment(IServiceProvider provider)
    {
        var deleteFromApim = provider.GetRequiredService<DeletePolicyFragmentFromApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeletePolicyFragment))
                                       ?.AddTag("policy_fragment.name", name);

            await deleteFromApim(name, cancellationToken);
        };
    }

    private static void ConfigureDeletePolicyFragmentFromApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeletePolicyFragmentFromApim);
    }

    private static DeletePolicyFragmentFromApim GetDeletePolicyFragmentFromApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, cancellationToken) =>
        {
            logger.LogInformation("Deleting policy fragment {PolicyFragmentName}...", name);

            await PolicyFragmentUri.From(name, serviceUri)
                                   .Delete(pipeline, cancellationToken);
        };
    }
}