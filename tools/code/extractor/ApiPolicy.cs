using Azure.Core.Pipeline;
using common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal delegate ValueTask ExtractApiPolicies(ApiName apiName, CancellationToken cancellationToken);

file delegate IAsyncEnumerable<(ApiPolicyName Name, ApiPolicyDto Dto)> ListApiPolicies(ApiName apiName, CancellationToken cancellationToken);

file delegate ValueTask WriteApiPolicyArtifacts(ApiPolicyName name, ApiPolicyDto dto, ApiName apiName, CancellationToken cancellationToken);

file delegate ValueTask WriteApiPolicyFile(ApiPolicyName name, ApiPolicyDto dto, ApiName apiName, CancellationToken cancellationToken);

file sealed class ExtractApiPoliciesHandler(ListApiPolicies list, WriteApiPolicyArtifacts writeArtifacts)
{
    public async ValueTask Handle(ApiName apiName, CancellationToken cancellationToken) =>
        await list(apiName, cancellationToken)
                .IterParallel(async apipolicy => await writeArtifacts(apipolicy.Name, apipolicy.Dto, apiName, cancellationToken),
                              cancellationToken);
}

file sealed class ListApiPoliciesHandler(ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    public IAsyncEnumerable<(ApiPolicyName, ApiPolicyDto)> Handle(ApiName apiName, CancellationToken cancellationToken) =>
        ApiPoliciesUri.From(apiName, serviceUri).List(pipeline, cancellationToken);
}

file sealed class WriteApiPolicyArtifactsHandler(WriteApiPolicyFile writePolicyFile)
{
    public async ValueTask Handle(ApiPolicyName name, ApiPolicyDto dto, ApiName apiName, CancellationToken cancellationToken)
    {
        await writePolicyFile(name, dto, apiName, cancellationToken);
    }
}

file sealed class WriteApiPolicyFileHandler(ILoggerFactory loggerFactory, ManagementServiceDirectory serviceDirectory)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(ApiPolicyName name, ApiPolicyDto dto, ApiName apiName, CancellationToken cancellationToken)
    {
        var policyFile = ApiPolicyFile.From(name, apiName, serviceDirectory);

        logger.LogInformation("Writing API policy file {ApiPolicyFile}...", policyFile);
        var policy = dto.Properties.Value ?? string.Empty;
        await policyFile.WritePolicy(policy, cancellationToken);
    }
}

internal static class ApiPolicyServices
{
    public static void ConfigureExtractApiPolicies(IServiceCollection services)
    {
        ConfigureListApiPolicies(services);
        ConfigureWriteApiPolicyArtifacts(services);

        services.TryAddSingleton<ExtractApiPoliciesHandler>();
        services.TryAddSingleton<ExtractApiPolicies>(provider => provider.GetRequiredService<ExtractApiPoliciesHandler>().Handle);
    }

    private static void ConfigureListApiPolicies(IServiceCollection services)
    {
        services.TryAddSingleton<ListApiPoliciesHandler>();
        services.TryAddSingleton<ListApiPolicies>(provider => provider.GetRequiredService<ListApiPoliciesHandler>().Handle);
    }

    private static void ConfigureWriteApiPolicyArtifacts(IServiceCollection services)
    {
        ConfigureWriteApiPolicyFile(services);

        services.TryAddSingleton<WriteApiPolicyArtifactsHandler>();
        services.TryAddSingleton<WriteApiPolicyArtifacts>(provider => provider.GetRequiredService<WriteApiPolicyArtifactsHandler>().Handle);
    }

    private static void ConfigureWriteApiPolicyFile(IServiceCollection services)
    {
        services.TryAddSingleton<WriteApiPolicyFileHandler>();
        services.TryAddSingleton<WriteApiPolicyFile>(provider => provider.GetRequiredService<WriteApiPolicyFileHandler>().Handle);
    }
}

file static class Common
{
    public static ILogger GetLogger(ILoggerFactory loggerFactory) =>
        loggerFactory.CreateLogger("ApiPolicyExtractor");
}