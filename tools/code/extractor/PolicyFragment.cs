using Azure.Core.Pipeline;
using common;
using LanguageExt;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal delegate ValueTask ExtractPolicyFragments(CancellationToken cancellationToken);

file delegate IAsyncEnumerable<(PolicyFragmentName Name, PolicyFragmentDto Dto)> ListPolicyFragments(CancellationToken cancellationToken);

file delegate bool ShouldExtractPolicyFragment(PolicyFragmentName name);

file delegate ValueTask WritePolicyFragmentArtifacts(PolicyFragmentName name, PolicyFragmentDto dto, CancellationToken cancellationToken);

file delegate ValueTask WritePolicyFragmentInformationFile(PolicyFragmentName name, PolicyFragmentDto dto, CancellationToken cancellationToken);

file delegate ValueTask WritePolicyFragmentPolicyFile(PolicyFragmentName name, PolicyFragmentDto dto, CancellationToken cancellationToken);

file sealed class ExtractPolicyFragmentsHandler(ListPolicyFragments list, ShouldExtractPolicyFragment shouldExtract, WritePolicyFragmentArtifacts writeArtifacts)
{
    public async ValueTask Handle(CancellationToken cancellationToken) =>
        await list(cancellationToken)
                .Where(policyfragment => shouldExtract(policyfragment.Name))
                .IterParallel(async policyfragment => await writeArtifacts(policyfragment.Name, policyfragment.Dto, cancellationToken),
                              cancellationToken);
}

file sealed class ListPolicyFragmentsHandler(ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    public IAsyncEnumerable<(PolicyFragmentName, PolicyFragmentDto)> Handle(CancellationToken cancellationToken) =>
        PolicyFragmentsUri.From(serviceUri).List(pipeline, cancellationToken);
}

file sealed class ShouldExtractPolicyFragmentHandler(ShouldExtractFactory shouldExtractFactory)
{
    public bool Handle(PolicyFragmentName name)
    {
        var shouldExtract = shouldExtractFactory.Create<PolicyFragmentName>();
        return shouldExtract(name);
    }
}

file sealed class WritePolicyFragmentArtifactsHandler(WritePolicyFragmentInformationFile writeInformationFile,
                                                      WritePolicyFragmentPolicyFile writePolicyFragmentPolicyFile)
{
    public async ValueTask Handle(PolicyFragmentName name, PolicyFragmentDto dto, CancellationToken cancellationToken)
    {
        await writeInformationFile(name, dto, cancellationToken);
        await writePolicyFragmentPolicyFile(name, dto, cancellationToken);
    }
}

file sealed class WritePolicyFragmentInformationFileHandler(ILoggerFactory loggerFactory, ManagementServiceDirectory serviceDirectory)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(PolicyFragmentName name, PolicyFragmentDto dto, CancellationToken cancellationToken)
    {
        var informationFile = PolicyFragmentInformationFile.From(name, serviceDirectory);

        logger.LogInformation("Writing policy fragment information file {PolicyFragmentInformationFile}...", informationFile);
        await informationFile.WriteDto(dto, cancellationToken);
    }
}

file sealed class WritePolicyFragmentPolicyFileHandler(ILoggerFactory loggerFactory, ManagementServiceDirectory serviceDirectory)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(PolicyFragmentName name, PolicyFragmentDto dto, CancellationToken cancellationToken)
    {
        var policyFile = PolicyFragmentPolicyFile.From(name, serviceDirectory);

        logger.LogInformation("Writing policy fragment policy file {PolicyFragmentPolicyFile}...", policyFile);
        var policy = dto.Properties.Value ?? string.Empty;
        await policyFile.WritePolicy(policy, cancellationToken);
    }
}

internal static class PolicyFragmentServices
{
    public static void ConfigureExtractPolicyFragments(IServiceCollection services)
    {
        ConfigureListPolicyFragments(services);
        ConfigureShouldExtractPolicyFragment(services);
        ConfigureWritePolicyFragmentArtifacts(services);

        services.TryAddSingleton<ExtractPolicyFragmentsHandler>();
        services.TryAddSingleton<ExtractPolicyFragments>(provider => provider.GetRequiredService<ExtractPolicyFragmentsHandler>().Handle);
    }

    private static void ConfigureListPolicyFragments(IServiceCollection services)
    {
        services.TryAddSingleton<ListPolicyFragmentsHandler>();
        services.TryAddSingleton<ListPolicyFragments>(provider => provider.GetRequiredService<ListPolicyFragmentsHandler>().Handle);
    }

    private static void ConfigureShouldExtractPolicyFragment(IServiceCollection services)
    {
        services.TryAddSingleton<ShouldExtractPolicyFragmentHandler>();
        services.TryAddSingleton<ShouldExtractPolicyFragment>(provider => provider.GetRequiredService<ShouldExtractPolicyFragmentHandler>().Handle);
    }

    private static void ConfigureWritePolicyFragmentArtifacts(IServiceCollection services)
    {
        ConfigureWritePolicyFragmentInformationFile(services);
        ConfigureWritePolicyFragmentPolicyFile(services);

        services.TryAddSingleton<WritePolicyFragmentArtifactsHandler>();
        services.TryAddSingleton<WritePolicyFragmentArtifacts>(provider => provider.GetRequiredService<WritePolicyFragmentArtifactsHandler>().Handle);
    }

    private static void ConfigureWritePolicyFragmentInformationFile(IServiceCollection services)
    {
        services.TryAddSingleton<WritePolicyFragmentInformationFileHandler>();
        services.TryAddSingleton<WritePolicyFragmentInformationFile>(provider => provider.GetRequiredService<WritePolicyFragmentInformationFileHandler>().Handle);
    }

    private static void ConfigureWritePolicyFragmentPolicyFile(IServiceCollection services)
    {
        services.TryAddSingleton<WritePolicyFragmentPolicyFileHandler>();
        services.TryAddSingleton<WritePolicyFragmentPolicyFile>(provider => provider.GetRequiredService<WritePolicyFragmentPolicyFileHandler>().Handle);
    }
}

file static class Common
{
    public static ILogger GetLogger(ILoggerFactory loggerFactory) =>
        loggerFactory.CreateLogger("PolicyFragmentExtractor");
}