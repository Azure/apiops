using Azure.Core.Pipeline;
using common;
using LanguageExt;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

public delegate ValueTask ExtractPolicyFragments(CancellationToken cancellationToken);
public delegate IAsyncEnumerable<(PolicyFragmentName Name, PolicyFragmentDto Dto)> ListPolicyFragments(CancellationToken cancellationToken);
public delegate bool ShouldExtractPolicyFragment(PolicyFragmentName name);
public delegate ValueTask WritePolicyFragmentArtifacts(PolicyFragmentName name, PolicyFragmentDto dto, CancellationToken cancellationToken);
public delegate ValueTask WritePolicyFragmentInformationFile(PolicyFragmentName name, PolicyFragmentDto dto, CancellationToken cancellationToken);
public delegate ValueTask WritePolicyFragmentPolicyFile(PolicyFragmentName name, PolicyFragmentDto dto, CancellationToken cancellationToken);

internal static class PolicyFragmentModule
{
    public static void ConfigureExtractPolicyFragments(IHostApplicationBuilder builder)
    {
        ConfigureListPolicyFragments(builder);
        ConfigureShouldExtractPolicyFragment(builder);
        ConfigureWritePolicyFragmentArtifacts(builder);

        builder.Services.TryAddSingleton(GetExtractPolicyFragments);
    }

    private static ExtractPolicyFragments GetExtractPolicyFragments(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListPolicyFragments>();
        var shouldExtract = provider.GetRequiredService<ShouldExtractPolicyFragment>();
        var writeArtifacts = provider.GetRequiredService<WritePolicyFragmentArtifacts>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractPolicyFragments));

            logger.LogInformation("Extracting policy fragments...");

            await list(cancellationToken)
                    .Where(policyfragment => shouldExtract(policyfragment.Name))
                    .IterParallel(async policyfragment => await writeArtifacts(policyfragment.Name, policyfragment.Dto, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureListPolicyFragments(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);
        PolicySpecificationModule.ConfigureDefaultPolicySpecification(builder);

        builder.Services.TryAddSingleton(GetListPolicyFragments);
    }

    private static ListPolicyFragments GetListPolicyFragments(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var policySpecificationFormat = provider.GetRequiredService<DefaultPolicySpecification>();

        return cancellationToken =>
            PolicyFragmentsUri.From(serviceUri)
                              .List(pipeline, cancellationToken, policySpecificationFormat.PolicyFormat);
    }

    private static void ConfigureShouldExtractPolicyFragment(IHostApplicationBuilder builder)
    {
        ShouldExtractModule.ConfigureShouldExtractFactory(builder);

        builder.Services.TryAddSingleton(GetShouldExtractPolicyFragment);
    }

    private static ShouldExtractPolicyFragment GetShouldExtractPolicyFragment(IServiceProvider provider)
    {
        var shouldExtractFactory = provider.GetRequiredService<ShouldExtractFactory>();

        var shouldExtract = shouldExtractFactory.Create<PolicyFragmentName>();

        return name => shouldExtract(name);
    }

    private static void ConfigureWritePolicyFragmentArtifacts(IHostApplicationBuilder builder)
    {
        ConfigureWritePolicyFragmentInformationFile(builder);
        ConfigureWritePolicyFragmentPolicyFile(builder);

        builder.Services.TryAddSingleton(GetWritePolicyFragmentArtifacts);
    }

    private static WritePolicyFragmentArtifacts GetWritePolicyFragmentArtifacts(IServiceProvider provider)
    {
        var writeInformationFile = provider.GetRequiredService<WritePolicyFragmentInformationFile>();
        var writePolicyFragmentPolicyFile = provider.GetRequiredService<WritePolicyFragmentPolicyFile>();

        return async (name, dto, cancellationToken) =>
        {
            await writeInformationFile(name, dto, cancellationToken);
            await writePolicyFragmentPolicyFile(name, dto, cancellationToken);
        };
    }

    private static void ConfigureWritePolicyFragmentInformationFile(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetWritePolicyFragmentInformationFile);
    }

    private static WritePolicyFragmentInformationFile GetWritePolicyFragmentInformationFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, cancellationToken) =>
        {
            var informationFile = PolicyFragmentInformationFile.From(name, serviceDirectory);

            logger.LogInformation("Writing policy fragment information file {PolicyFragmentInformationFile}...", informationFile);

            // Remove policy contents from DTO, as these will be written to the policy file
            var updatedDto = dto with { Properties = dto.Properties with { Format = null, Value = null } };
            await informationFile.WriteDto(updatedDto, cancellationToken);
        };
    }

    private static void ConfigureWritePolicyFragmentPolicyFile(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetWritePolicyFragmentPolicyFile);
    }

    private static WritePolicyFragmentPolicyFile GetWritePolicyFragmentPolicyFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, cancellationToken) =>
        {
            var policyFile = PolicyFragmentPolicyFile.From(name, serviceDirectory);

            logger.LogInformation("Writing policy fragment policy file {PolicyFragmentPolicyFile}...", policyFile);
            var policy = dto.Properties.Value ?? string.Empty;
            await policyFile.WritePolicy(policy, cancellationToken);
        };
    }
}