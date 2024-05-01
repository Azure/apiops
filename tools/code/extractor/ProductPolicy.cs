using Azure.Core.Pipeline;
using common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal delegate ValueTask ExtractProductPolicies(ProductName productName, CancellationToken cancellationToken);

file delegate IAsyncEnumerable<(ProductPolicyName Name, ProductPolicyDto Dto)> ListProductPolicies(ProductName productName, CancellationToken cancellationToken);

file delegate ValueTask WriteProductPolicyArtifacts(ProductPolicyName name, ProductPolicyDto dto, ProductName productName, CancellationToken cancellationToken);

file delegate ValueTask WriteProductPolicyFile(ProductPolicyName name, ProductPolicyDto dto, ProductName productName, CancellationToken cancellationToken);

file sealed class ExtractProductPoliciesHandler(ListProductPolicies list, WriteProductPolicyArtifacts writeArtifacts)
{
    public async ValueTask Handle(ProductName productName, CancellationToken cancellationToken) =>
        await list(productName, cancellationToken)
                .IterParallel(async productpolicy => await writeArtifacts(productpolicy.Name, productpolicy.Dto, productName, cancellationToken),
                              cancellationToken);
}

file sealed class ListProductPoliciesHandler(ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    public IAsyncEnumerable<(ProductPolicyName, ProductPolicyDto)> Handle(ProductName productName, CancellationToken cancellationToken) =>
        ProductPoliciesUri.From(productName, serviceUri).List(pipeline, cancellationToken);
}

file sealed class WriteProductPolicyArtifactsHandler(WriteProductPolicyFile writePolicyFile)
{
    public async ValueTask Handle(ProductPolicyName name, ProductPolicyDto dto, ProductName productName, CancellationToken cancellationToken)
    {
        await writePolicyFile(name, dto, productName, cancellationToken);
    }
}

file sealed class WriteProductPolicyFileHandler(ILoggerFactory loggerFactory, ManagementServiceDirectory serviceDirectory)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(ProductPolicyName name, ProductPolicyDto dto, ProductName productName, CancellationToken cancellationToken)
    {
        var policyFile = ProductPolicyFile.From(name, productName, serviceDirectory);

        logger.LogInformation("Writing product policy file {ProductPolicyFile}...", policyFile);
        var policy = dto.Properties.Value ?? string.Empty;
        await policyFile.WritePolicy(policy, cancellationToken);
    }
}

internal static class ProductPolicyServices
{
    public static void ConfigureExtractProductPolicies(IServiceCollection services)
    {
        ConfigureListProductPolicies(services);
        ConfigureWriteProductPolicyArtifacts(services);

        services.TryAddSingleton<ExtractProductPoliciesHandler>();
        services.TryAddSingleton<ExtractProductPolicies>(provider => provider.GetRequiredService<ExtractProductPoliciesHandler>().Handle);
    }

    private static void ConfigureListProductPolicies(IServiceCollection services)
    {
        services.TryAddSingleton<ListProductPoliciesHandler>();
        services.TryAddSingleton<ListProductPolicies>(provider => provider.GetRequiredService<ListProductPoliciesHandler>().Handle);
    }

    private static void ConfigureWriteProductPolicyArtifacts(IServiceCollection services)
    {
        ConfigureWriteProductPolicyFile(services);

        services.TryAddSingleton<WriteProductPolicyArtifactsHandler>();
        services.TryAddSingleton<WriteProductPolicyArtifacts>(provider => provider.GetRequiredService<WriteProductPolicyArtifactsHandler>().Handle);
    }

    private static void ConfigureWriteProductPolicyFile(IServiceCollection services)
    {
        services.TryAddSingleton<WriteProductPolicyFileHandler>();
        services.TryAddSingleton<WriteProductPolicyFile>(provider => provider.GetRequiredService<WriteProductPolicyFileHandler>().Handle);
    }
}

file static class Common
{
    public static ILogger GetLogger(ILoggerFactory loggerFactory) =>
        loggerFactory.CreateLogger("ProductPolicyExtractor");
}