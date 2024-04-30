using Azure.Core.Pipeline;
using common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal delegate ValueTask ExtractApiOperationPolicies(ApiOperationName apiOperationName, ApiName apiName, CancellationToken cancellationToken);

file delegate IAsyncEnumerable<(ApiOperationPolicyName Name, ApiOperationPolicyDto Dto)> ListApiOperationPolicies(ApiOperationName apiOperationName, ApiName apiName, CancellationToken cancellationToken);

file delegate ValueTask WriteApiOperationPolicyArtifacts(ApiOperationPolicyName name, ApiOperationPolicyDto dto, ApiOperationName apiOperationName, ApiName apiName, CancellationToken cancellationToken);

file delegate ValueTask WriteApiOperationPolicyFile(ApiOperationPolicyName name, ApiOperationPolicyDto dto, ApiOperationName apiOperationName, ApiName apiName, CancellationToken cancellationToken);

file sealed class ExtractApiOperationPoliciesHandler(ListApiOperationPolicies list, WriteApiOperationPolicyArtifacts writeArtifacts)
{
    public async ValueTask Handle(ApiOperationName apiOperationName, ApiName apiName, CancellationToken cancellationToken) =>
        await list(apiOperationName, apiName, cancellationToken)
                .IterParallel(async apiOperationpolicy => await writeArtifacts(apiOperationpolicy.Name, apiOperationpolicy.Dto, apiOperationName, apiName, cancellationToken),
                              cancellationToken);
}

file sealed class ListApiOperationPoliciesHandler(ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    public IAsyncEnumerable<(ApiOperationPolicyName, ApiOperationPolicyDto)> Handle(ApiOperationName apiOperationName, ApiName apiName, CancellationToken cancellationToken) =>
        ApiOperationPoliciesUri.From(apiOperationName, apiName, serviceUri).List(pipeline, cancellationToken);
}

file sealed class WriteApiOperationPolicyArtifactsHandler(WriteApiOperationPolicyFile writePolicyFile)
{
    public async ValueTask Handle(ApiOperationPolicyName name, ApiOperationPolicyDto dto, ApiOperationName apiOperationName, ApiName apiName, CancellationToken cancellationToken)
    {
        await writePolicyFile(name, dto, apiOperationName, apiName, cancellationToken);
    }
}

file sealed class WriteApiOperationPolicyFileHandler(ILoggerFactory loggerFactory, ManagementServiceDirectory serviceDirectory)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(ApiOperationPolicyName name, ApiOperationPolicyDto dto, ApiOperationName apiOperationName, ApiName apiName, CancellationToken cancellationToken)
    {
        var policyFile = ApiOperationPolicyFile.From(name, apiOperationName, apiName, serviceDirectory);

        logger.LogInformation("Writing API operation policy file {ApiOperationPolicyFile}...", policyFile);
        var policy = dto.Properties.Value ?? string.Empty;
        await policyFile.WritePolicy(policy, cancellationToken);
    }
}

internal static class ApiOperationPolicyServices
{
    public static void ConfigureExtractApiOperationPolicies(IServiceCollection services)
    {
        ConfigureListApiOperationPolicies(services);
        ConfigureWriteApiOperationPolicyArtifacts(services);

        services.TryAddSingleton<ExtractApiOperationPoliciesHandler>();
        services.TryAddSingleton<ExtractApiOperationPolicies>(provider => provider.GetRequiredService<ExtractApiOperationPoliciesHandler>().Handle);
    }

    private static void ConfigureListApiOperationPolicies(IServiceCollection services)
    {
        services.TryAddSingleton<ListApiOperationPoliciesHandler>();
        services.TryAddSingleton<ListApiOperationPolicies>(provider => provider.GetRequiredService<ListApiOperationPoliciesHandler>().Handle);
    }

    private static void ConfigureWriteApiOperationPolicyArtifacts(IServiceCollection services)
    {
        ConfigureWriteApiOperationPolicyFile(services);

        services.TryAddSingleton<WriteApiOperationPolicyArtifactsHandler>();
        services.TryAddSingleton<WriteApiOperationPolicyArtifacts>(provider => provider.GetRequiredService<WriteApiOperationPolicyArtifactsHandler>().Handle);
    }

    private static void ConfigureWriteApiOperationPolicyFile(IServiceCollection services)
    {
        services.TryAddSingleton<WriteApiOperationPolicyFileHandler>();
        services.TryAddSingleton<WriteApiOperationPolicyFile>(provider => provider.GetRequiredService<WriteApiOperationPolicyFileHandler>().Handle);
    }
}

file static class Common
{
    public static ILogger GetLogger(ILoggerFactory loggerFactory) =>
        loggerFactory.CreateLogger("ApiOperationPolicyExtractor");
}