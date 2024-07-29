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

public delegate ValueTask PutServicePolicies(CancellationToken cancellationToken);
public delegate Option<ServicePolicyName> TryParseServicePolicyName(FileInfo file);
public delegate bool IsServicePolicyNameInSourceControl(ServicePolicyName name);
public delegate ValueTask PutServicePolicy(ServicePolicyName name, CancellationToken cancellationToken);
public delegate ValueTask<Option<ServicePolicyDto>> FindServicePolicyDto(ServicePolicyName name, CancellationToken cancellationToken);
public delegate ValueTask PutServicePolicyInApim(ServicePolicyName name, ServicePolicyDto dto, CancellationToken cancellationToken);
public delegate ValueTask DeleteServicePolicies(CancellationToken cancellationToken);
public delegate ValueTask DeleteServicePolicy(ServicePolicyName name, CancellationToken cancellationToken);
public delegate ValueTask DeleteServicePolicyFromApim(ServicePolicyName name, CancellationToken cancellationToken);

internal static class ServicePolicyModule
{
    public static void ConfigurePutServicePolicies(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseServicePolicyName(builder);
        ConfigureIsServicePolicyNameInSourceControl(builder);
        ConfigurePutServicePolicy(builder);

        builder.Services.TryAddSingleton(GetPutServicePolicies);
    }

    private static PutServicePolicies GetPutServicePolicies(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseServicePolicyName>();
        var isNameInSourceControl = provider.GetRequiredService<IsServicePolicyNameInSourceControl>();
        var put = provider.GetRequiredService<PutServicePolicy>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(PutServicePolicies));

            logger.LogInformation("Putting service policies...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(isNameInSourceControl.Invoke)
                    .Distinct()
                    .IterParallel(put.Invoke, cancellationToken);
        };
    }

    private static void ConfigureTryParseServicePolicyName(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetTryParseServicePolicyName);
    }

    private static TryParseServicePolicyName GetTryParseServicePolicyName(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return file => from policyFile in ServicePolicyFile.TryParse(file, serviceDirectory)
                       select policyFile.Name;
    }

    private static void ConfigureIsServicePolicyNameInSourceControl(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetArtifactFiles(builder);
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetIsServicePolicyNameInSourceControl);
    }

    private static IsServicePolicyNameInSourceControl GetIsServicePolicyNameInSourceControl(IServiceProvider provider)
    {
        var getArtifactFiles = provider.GetRequiredService<GetArtifactFiles>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return doesPolicyFileExist;
        
        bool doesPolicyFileExist(ServicePolicyName name)
        {
            var artifactFiles = getArtifactFiles();
            var policyFile = ServicePolicyFile.From(name, serviceDirectory);

            return artifactFiles.Contains(policyFile.ToFileInfo());
        }
    }

    private static void ConfigurePutServicePolicy(IHostApplicationBuilder builder)
    {
        ConfigureFindServicePolicyDto(builder);
        ConfigurePutServicePolicyInApim(builder);

        builder.Services.TryAddSingleton(GetPutServicePolicy);
    }

    private static PutServicePolicy GetPutServicePolicy(IServiceProvider provider)
    {
        var findDto = provider.GetRequiredService<FindServicePolicyDto>();
        var putInApim = provider.GetRequiredService<PutServicePolicyInApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutServicePolicy))
                                       ?.AddTag("service_policy.name", name);

            var dtoOption = await findDto(name, cancellationToken);
            await dtoOption.IterTask(async dto => await putInApim(name, dto, cancellationToken));
        };
    }

    private static void ConfigureFindServicePolicyDto(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);
        CommonModule.ConfigureTryGetFileContents(builder);
        OverrideDtoModule.ConfigureOverrideDtoFactory(builder);

        builder.Services.TryAddSingleton(GetFindServicePolicyDto);
    }

    private static FindServicePolicyDto GetFindServicePolicyDto(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var tryGetFileContents = provider.GetRequiredService<TryGetFileContents>();
        var overrideFactory = provider.GetRequiredService<OverrideDtoFactory>();

        var overrideDto = overrideFactory.Create<ServicePolicyName, ServicePolicyDto>();

        return async (name, cancellationToken) =>
        {
            var contentsOption = await tryGetPolicyContents(name, cancellationToken);

            return from contents in contentsOption
                   let dto = new ServicePolicyDto
                   {
                       Properties = new ServicePolicyDto.ServicePolicyContract
                       {
                           Format = "rawxml",
                           Value = contents.ToString()
                       }
                   }
                   let overrideDto = overrideFactory.Create<ServicePolicyName, ServicePolicyDto>()
                   select overrideDto(name, dto);
        };
        
        async ValueTask<Option<BinaryData>> tryGetPolicyContents(ServicePolicyName name, CancellationToken cancellationToken)
        {
            var policyFile = ServicePolicyFile.From(name, serviceDirectory);

            return await tryGetFileContents(policyFile.ToFileInfo(), cancellationToken);
        }
    }

    private static void ConfigurePutServicePolicyInApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutServicePolicyInApim);
    }

    private static PutServicePolicyInApim GetPutServicePolicyInApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, cancellationToken) =>
        {
            logger.LogInformation("Putting service policy {ServicePolicyName}...", name);

            await ServicePolicyUri.From(name, serviceUri)
                                  .PutDto(dto, pipeline, cancellationToken);
        };
    }

    public static void ConfigureDeleteServicePolicies(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseServicePolicyName(builder);
        ConfigureIsServicePolicyNameInSourceControl(builder);
        ConfigureDeleteServicePolicy(builder);

        builder.Services.TryAddSingleton(GetDeleteServicePolicies);
    }

    private static DeleteServicePolicies GetDeleteServicePolicies(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseServicePolicyName>();
        var isNameInSourceControl = provider.GetRequiredService<IsServicePolicyNameInSourceControl>();
        var delete = provider.GetRequiredService<DeleteServicePolicy>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteServicePolicies));

            logger.LogInformation("Deleting service policies...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(name => isNameInSourceControl(name) is false)
                    .Distinct()
                    .IterParallel(delete.Invoke, cancellationToken);
        };
    }

    private static void ConfigureDeleteServicePolicy(IHostApplicationBuilder builder)
    {
        ConfigureDeleteServicePolicyFromApim(builder);

        builder.Services.TryAddSingleton(GetDeleteServicePolicy);
    }

    private static DeleteServicePolicy GetDeleteServicePolicy(IServiceProvider provider)
    {
        var deleteFromApim = provider.GetRequiredService<DeleteServicePolicyFromApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteServicePolicy))
                                       ?.AddTag("service_policy.name", name);

            await deleteFromApim(name, cancellationToken);
        };
    }

    private static void ConfigureDeleteServicePolicyFromApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteServicePolicyFromApim);
    }

    private static DeleteServicePolicyFromApim GetDeleteServicePolicyFromApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, cancellationToken) =>
        {
            logger.LogInformation("Deleting service policy {ServicePolicyName}...", name);

            await ServicePolicyUri.From(name, serviceUri)
                                  .Delete(pipeline, cancellationToken);
        };
    }
}