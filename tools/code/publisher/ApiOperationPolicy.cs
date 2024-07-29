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

public delegate ValueTask PutApiOperationPolicies(CancellationToken cancellationToken);
public delegate Option<(ApiOperationPolicyName Name, ApiOperationName OperationName, ApiName ApiName)> TryParseApiOperationPolicyName(FileInfo file);
public delegate bool IsApiOperationPolicyNameInSourceControl(ApiOperationPolicyName name, ApiOperationName operationName, ApiName apiName);
public delegate ValueTask PutApiOperationPolicy(ApiOperationPolicyName name, ApiOperationName operationName, ApiName apiName, CancellationToken cancellationToken);
public delegate ValueTask<Option<ApiOperationPolicyDto>> FindApiOperationPolicyDto(ApiOperationPolicyName name, ApiOperationName operationName, ApiName apiName, CancellationToken cancellationToken);
public delegate ValueTask PutApiOperationPolicyInApim(ApiOperationPolicyName name, ApiOperationPolicyDto dto, ApiOperationName operationName, ApiName apiName, CancellationToken cancellationToken);
public delegate ValueTask DeleteApiOperationPolicies(CancellationToken cancellationToken);
public delegate ValueTask DeleteApiOperationPolicy(ApiOperationPolicyName name, ApiOperationName operationName, ApiName apiName, CancellationToken cancellationToken);
public delegate ValueTask DeleteApiOperationPolicyFromApim(ApiOperationPolicyName name, ApiOperationName operationName, ApiName apiName, CancellationToken cancellationToken);

internal static class ApiOperationPolicyModule
{
    public static void ConfigurePutApiOperationPolicies(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseApiOperationPolicyName(builder);
        ConfigureIsApiOperationPolicyNameInSourceControl(builder);
        ConfigurePutApiOperationPolicy(builder);

        builder.Services.TryAddSingleton(GetPutApiOperationPolicies);
    }

    private static PutApiOperationPolicies GetPutApiOperationPolicies(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseApiOperationPolicyName>();
        var isNameInSourceControl = provider.GetRequiredService<IsApiOperationPolicyNameInSourceControl>();
        var put = provider.GetRequiredService<PutApiOperationPolicy>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(PutApiOperationPolicies));

            logger.LogInformation("Putting API policies...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(policy => isNameInSourceControl(policy.Name, policy.OperationName, policy.ApiName))
                    .Distinct()
                    .IterParallel(async policy => await put(policy.Name, policy.OperationName, policy.ApiName, cancellationToken), cancellationToken);
        };
    }

    private static void ConfigureTryParseApiOperationPolicyName(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetTryParseApiOperationPolicyName);
    }

    private static TryParseApiOperationPolicyName GetTryParseApiOperationPolicyName(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return file => from policyFile in ApiOperationPolicyFile.TryParse(file, serviceDirectory)
                       select (policyFile.Name, policyFile.Parent.Name, policyFile.Parent.Parent.Parent.Name);
    }

    private static void ConfigureIsApiOperationPolicyNameInSourceControl(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetArtifactFiles(builder);
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetIsApiOperationPolicyNameInSourceControl);
    }

    private static IsApiOperationPolicyNameInSourceControl GetIsApiOperationPolicyNameInSourceControl(IServiceProvider provider)
    {
        var getArtifactFiles = provider.GetRequiredService<GetArtifactFiles>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return doesPolicyFileExist;

        bool doesPolicyFileExist(ApiOperationPolicyName name, ApiOperationName operationName, ApiName apiName)
        {
            var artifactFiles = getArtifactFiles();
            var policyFile = ApiOperationPolicyFile.From(name, operationName, apiName, serviceDirectory);

            return artifactFiles.Contains(policyFile.ToFileInfo());
        }
    }

    private static void ConfigurePutApiOperationPolicy(IHostApplicationBuilder builder)
    {
        ConfigureFindApiOperationPolicyDto(builder);
        ConfigurePutApiOperationPolicyInApim(builder);

        builder.Services.TryAddSingleton(GetPutApiOperationPolicy);
    }

    private static PutApiOperationPolicy GetPutApiOperationPolicy(IServiceProvider provider)
    {
        var findDto = provider.GetRequiredService<FindApiOperationPolicyDto>();
        var putInApim = provider.GetRequiredService<PutApiOperationPolicyInApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, operationName, apiName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutApiOperationPolicy))
                                       ?.AddTag("api_policy.name", name)
                                       ?.AddTag("api_operation.name", operationName)
                                       ?.AddTag("api.name", apiName);

            var dtoOption = await findDto(name, operationName, apiName, cancellationToken);
            await dtoOption.IterTask(async dto => await putInApim(name, dto, operationName, apiName, cancellationToken));
        };
    }

    private static void ConfigureFindApiOperationPolicyDto(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);
        CommonModule.ConfigureTryGetFileContents(builder);

        builder.Services.TryAddSingleton(GetFindApiOperationPolicyDto);
    }

    private static FindApiOperationPolicyDto GetFindApiOperationPolicyDto(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var tryGetFileContents = provider.GetRequiredService<TryGetFileContents>();

        return async (name, operationName, apiName, cancellationToken) =>
        {
            var contentsOption = await tryGetPolicyContents(name, operationName, apiName, cancellationToken);

            return from contents in contentsOption
                   select new ApiOperationPolicyDto
                   {
                       Properties = new ApiOperationPolicyDto.ApiOperationPolicyContract
                       {
                           Format = "rawxml",
                           Value = contents.ToString()
                       }
                   };
        };

        async ValueTask<Option<BinaryData>> tryGetPolicyContents(ApiOperationPolicyName name, ApiOperationName operationName, ApiName apiName, CancellationToken cancellationToken)
        {
            var policyFile = ApiOperationPolicyFile.From(name, operationName, apiName, serviceDirectory);

            return await tryGetFileContents(policyFile.ToFileInfo(), cancellationToken);
        }
    }

    private static void ConfigurePutApiOperationPolicyInApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutApiOperationPolicyInApim);
    }

    private static PutApiOperationPolicyInApim GetPutApiOperationPolicyInApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, operationName, apiName, cancellationToken) =>
        {
            logger.LogInformation("Putting policy {ApiOperationPolicyName} for operation {ApiOperationName} in API {ApiName}...", name, operationName, apiName);

            await ApiOperationPolicyUri.From(name, operationName, apiName, serviceUri)
                              .PutDto(dto, pipeline, cancellationToken);
        };
    }

    public static void ConfigureDeleteApiOperationPolicies(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseApiOperationPolicyName(builder);
        ConfigureIsApiOperationPolicyNameInSourceControl(builder);
        ConfigureDeleteApiOperationPolicy(builder);

        builder.Services.TryAddSingleton(GetDeleteApiOperationPolicies);
    }

    private static DeleteApiOperationPolicies GetDeleteApiOperationPolicies(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseApiOperationPolicyName>();
        var isNameInSourceControl = provider.GetRequiredService<IsApiOperationPolicyNameInSourceControl>();
        var delete = provider.GetRequiredService<DeleteApiOperationPolicy>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteApiOperationPolicies));

            logger.LogInformation("Deleting API policies...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(policy => isNameInSourceControl(policy.Name, policy.OperationName, policy.ApiName) is false)
                    .Distinct()
                    .IterParallel(async policy => await delete(policy.Name, policy.OperationName, policy.ApiName, cancellationToken), cancellationToken);
        };
    }

    private static void ConfigureDeleteApiOperationPolicy(IHostApplicationBuilder builder)
    {
        ConfigureDeleteApiOperationPolicyFromApim(builder);

        builder.Services.TryAddSingleton(GetDeleteApiOperationPolicy);
    }

    private static DeleteApiOperationPolicy GetDeleteApiOperationPolicy(IServiceProvider provider)
    {
        var deleteFromApim = provider.GetRequiredService<DeleteApiOperationPolicyFromApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, operationName, apiName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteApiOperationPolicy))
                                       ?.AddTag("api_policy.name", name)
                                       ?.AddTag("api_operation.name", operationName)
                                       ?.AddTag("api.name", apiName);

            await deleteFromApim(name, operationName, apiName, cancellationToken);
        };
    }

    private static void ConfigureDeleteApiOperationPolicyFromApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteApiOperationPolicyFromApim);
    }

    private static DeleteApiOperationPolicyFromApim GetDeleteApiOperationPolicyFromApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, operationName, apiName, cancellationToken) =>
        {
            logger.LogInformation("Deleting policy {ApiOperationPolicyName} from operation {ApiOperationName} in API {Apiname}...", name, operationName, apiName);

            await ApiOperationPolicyUri.From(name, operationName, apiName, serviceUri)
                                       .Delete(pipeline, cancellationToken);
        };
    }
}