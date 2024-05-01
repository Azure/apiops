using Azure.Core.Pipeline;
using common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal delegate ValueTask ExtractServicePolicies(CancellationToken cancellationToken);

file delegate IAsyncEnumerable<(ServicePolicyName Name, ServicePolicyDto Dto)> ListServicePolicies(CancellationToken cancellationToken);

file delegate ValueTask WriteServicePolicyArtifacts(ServicePolicyName name, ServicePolicyDto dto, CancellationToken cancellationToken);

file delegate ValueTask WriteServicePolicyFile(ServicePolicyName name, ServicePolicyDto dto, CancellationToken cancellationToken);

file sealed class ExtractServicePoliciesHandler(ListServicePolicies list, WriteServicePolicyArtifacts writeArtifacts)
{
    public async ValueTask Handle(CancellationToken cancellationToken) =>
        await list(cancellationToken)
                .IterParallel(async servicepolicy => await writeArtifacts(servicepolicy.Name, servicepolicy.Dto, cancellationToken),
                              cancellationToken);
}

file sealed class ListServicePoliciesHandler(ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    public IAsyncEnumerable<(ServicePolicyName, ServicePolicyDto)> Handle(CancellationToken cancellationToken) =>
        ServicePoliciesUri.From(serviceUri).List(pipeline, cancellationToken);
}

file sealed class WriteServicePolicyArtifactsHandler(WriteServicePolicyFile writePolicyFile)
{
    public async ValueTask Handle(ServicePolicyName name, ServicePolicyDto dto, CancellationToken cancellationToken)
    {
        await writePolicyFile(name, dto, cancellationToken);
    }
}

file sealed class WriteServicePolicyFileHandler(ILoggerFactory loggerFactory, ManagementServiceDirectory serviceDirectory)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(ServicePolicyName name, ServicePolicyDto dto, CancellationToken cancellationToken)
    {
        var policyFile = ServicePolicyFile.From(name, serviceDirectory);

        logger.LogInformation("Writing service policy file {ServicePolicyFile}...", policyFile);
        var policy = dto.Properties.Value ?? string.Empty;
        await policyFile.WritePolicy(policy, cancellationToken);
    }
}

internal static class ServicePolicyServices
{
    public static void ConfigureExtractServicePolicies(IServiceCollection services)
    {
        ConfigureListServicePolicies(services);
        ConfigureWriteServicePolicyArtifacts(services);

        services.TryAddSingleton<ExtractServicePoliciesHandler>();
        services.TryAddSingleton<ExtractServicePolicies>(provider => provider.GetRequiredService<ExtractServicePoliciesHandler>().Handle);
    }

    private static void ConfigureListServicePolicies(IServiceCollection services)
    {
        services.TryAddSingleton<ListServicePoliciesHandler>();
        services.TryAddSingleton<ListServicePolicies>(provider => provider.GetRequiredService<ListServicePoliciesHandler>().Handle);
    }

    private static void ConfigureWriteServicePolicyArtifacts(IServiceCollection services)
    {
        ConfigureWriteServicePolicyFile(services);

        services.TryAddSingleton<WriteServicePolicyArtifactsHandler>();
        services.TryAddSingleton<WriteServicePolicyArtifacts>(provider => provider.GetRequiredService<WriteServicePolicyArtifactsHandler>().Handle);
    }

    private static void ConfigureWriteServicePolicyFile(IServiceCollection services)
    {
        services.TryAddSingleton<WriteServicePolicyFileHandler>();
        services.TryAddSingleton<WriteServicePolicyFile>(provider => provider.GetRequiredService<WriteServicePolicyFileHandler>().Handle);
    }
}

file static class Common
{
    public static ILogger GetLogger(ILoggerFactory loggerFactory) =>
        loggerFactory.CreateLogger("ServicePolicyExtractor");
}