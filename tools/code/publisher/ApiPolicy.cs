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

public delegate ValueTask PutApiPolicies(CancellationToken cancellationToken);
public delegate Option<(ApiPolicyName Name, ApiName ApiName)> TryParseApiPolicyName(FileInfo file);
public delegate bool IsApiPolicyNameInSourceControl(ApiPolicyName name, ApiName apiName);
public delegate ValueTask PutApiPolicy(ApiPolicyName name, ApiName apiName, CancellationToken cancellationToken);
public delegate ValueTask<Option<ApiPolicyDto>> FindApiPolicyDto(ApiPolicyName name, ApiName apiName, CancellationToken cancellationToken);
public delegate ValueTask PutApiPolicyInApim(ApiPolicyName name, ApiPolicyDto dto, ApiName apiName, CancellationToken cancellationToken);
public delegate ValueTask DeleteApiPolicies(CancellationToken cancellationToken);
public delegate ValueTask DeleteApiPolicy(ApiPolicyName name, ApiName apiName, CancellationToken cancellationToken);
public delegate ValueTask DeleteApiPolicyFromApim(ApiPolicyName name, ApiName apiName, CancellationToken cancellationToken);

internal static class ApiPolicyModule
{
    public static void ConfigurePutApiPolicies(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseApiPolicyName(builder);
        ConfigureIsApiPolicyNameInSourceControl(builder);
        ConfigurePutApiPolicy(builder);

        builder.Services.TryAddSingleton(GetPutApiPolicies);
    }

    private static PutApiPolicies GetPutApiPolicies(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseApiPolicyName>();
        var isNameInSourceControl = provider.GetRequiredService<IsApiPolicyNameInSourceControl>();
        var put = provider.GetRequiredService<PutApiPolicy>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(PutApiPolicies));

            logger.LogInformation("Putting API policies...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(policy => isNameInSourceControl(policy.Name, policy.ApiName))
                    .Distinct()
                    .IterParallel(put.Invoke, cancellationToken);
        };
    }

    private static void ConfigureTryParseApiPolicyName(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetTryParseApiPolicyName);
    }

    private static TryParseApiPolicyName GetTryParseApiPolicyName(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return file => from policyFile in ApiPolicyFile.TryParse(file, serviceDirectory)
                       select (policyFile.Name, policyFile.Parent.Name);
    }

    private static void ConfigureIsApiPolicyNameInSourceControl(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetArtifactFiles(builder);
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetIsApiPolicyNameInSourceControl);
    }

    private static IsApiPolicyNameInSourceControl GetIsApiPolicyNameInSourceControl(IServiceProvider provider)
    {
        var getArtifactFiles = provider.GetRequiredService<GetArtifactFiles>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return doesPolicyFileExist;

        bool doesPolicyFileExist(ApiPolicyName name, ApiName apiName)
        {
            var artifactFiles = getArtifactFiles();
            var policyFile = ApiPolicyFile.From(name, apiName, serviceDirectory);

            return artifactFiles.Contains(policyFile.ToFileInfo());
        }
    }

    private static void ConfigurePutApiPolicy(IHostApplicationBuilder builder)
    {
        ConfigureFindApiPolicyDto(builder);
        ConfigurePutApiPolicyInApim(builder);

        builder.Services.TryAddSingleton(GetPutApiPolicy);
    }

    private static PutApiPolicy GetPutApiPolicy(IServiceProvider provider)
    {
        var findDto = provider.GetRequiredService<FindApiPolicyDto>();
        var putInApim = provider.GetRequiredService<PutApiPolicyInApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, apiName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutApiPolicy))
                                       ?.AddTag("api_policy.name", name)
                                       ?.AddTag("api.name", apiName);

            var dtoOption = await findDto(name, apiName, cancellationToken);
            await dtoOption.IterTask(async dto => await putInApim(name, dto, apiName, cancellationToken));
        };
    }

    private static void ConfigureFindApiPolicyDto(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);
        CommonModule.ConfigureTryGetFileContents(builder);

        builder.Services.TryAddSingleton(GetFindApiPolicyDto);
    }

    private static FindApiPolicyDto GetFindApiPolicyDto(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var tryGetFileContents = provider.GetRequiredService<TryGetFileContents>();

        return async (name, apiName, cancellationToken) =>
        {
            var contentsOption = await tryGetPolicyContents(name, apiName, cancellationToken);

            return from contents in contentsOption
                   select new ApiPolicyDto
                   {
                       Properties = new ApiPolicyDto.ApiPolicyContract
                       {
                           Format = "rawxml",
                           Value = contents.ToString()
                       }
                   };
        };

        async ValueTask<Option<BinaryData>> tryGetPolicyContents(ApiPolicyName name, ApiName apiName, CancellationToken cancellationToken)
        {
            var policyFile = ApiPolicyFile.From(name, apiName, serviceDirectory);

            return await tryGetFileContents(policyFile.ToFileInfo(), cancellationToken);
        }
    }

    private static void ConfigurePutApiPolicyInApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutApiPolicyInApim);
    }

    private static PutApiPolicyInApim GetPutApiPolicyInApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, apiName, cancellationToken) =>
        {
            logger.LogInformation("Putting policy {ApiPolicyName} for API {ApiName}...", name, apiName);

            await ApiPolicyUri.From(name, apiName, serviceUri)
                              .PutDto(dto, pipeline, cancellationToken);
        };
    }

    public static void ConfigureDeleteApiPolicies(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseApiPolicyName(builder);
        ConfigureIsApiPolicyNameInSourceControl(builder);
        ConfigureDeleteApiPolicy(builder);

        builder.Services.TryAddSingleton(GetDeleteApiPolicies);
    }

    private static DeleteApiPolicies GetDeleteApiPolicies(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseApiPolicyName>();
        var isNameInSourceControl = provider.GetRequiredService<IsApiPolicyNameInSourceControl>();
        var delete = provider.GetRequiredService<DeleteApiPolicy>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteApiPolicies));

            logger.LogInformation("Deleting API policies...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(policy => isNameInSourceControl(policy.Name, policy.ApiName) is false)
                    .Distinct()
                    .IterParallel(delete.Invoke, cancellationToken);
        };
    }

    private static void ConfigureDeleteApiPolicy(IHostApplicationBuilder builder)
    {
        ConfigureDeleteApiPolicyFromApim(builder);

        builder.Services.TryAddSingleton(GetDeleteApiPolicy);
    }

    private static DeleteApiPolicy GetDeleteApiPolicy(IServiceProvider provider)
    {
        var deleteFromApim = provider.GetRequiredService<DeleteApiPolicyFromApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, apiName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteApiPolicy))
                                       ?.AddTag("api_policy.name", name)
                                       ?.AddTag("api.name", apiName);

            await deleteFromApim(name, apiName, cancellationToken);
        };
    }

    private static void ConfigureDeleteApiPolicyFromApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteApiPolicyFromApim);
    }

    private static DeleteApiPolicyFromApim GetDeleteApiPolicyFromApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, apiName, cancellationToken) =>
        {
            logger.LogInformation("Deleting policy {ApiPolicyName} from API {ApiName}...", name, apiName);

            await ApiPolicyUri.From(name, apiName, serviceUri)
                              .Delete(pipeline, cancellationToken);
        };
    }
}