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
public delegate ValueTask WritePolicyFragmentArtifacts(PolicyFragmentName name, PolicyFragmentDto dto, CancellationToken cancellationToken);
public delegate ValueTask WritePolicyFragmentInformationFile(PolicyFragmentName name, PolicyFragmentDto dto, CancellationToken cancellationToken);
public delegate ValueTask WritePolicyFragmentPolicyFile(PolicyFragmentName name, PolicyFragmentDto dto, CancellationToken cancellationToken);

internal static class PolicyFragmentModule
{
    public static void ConfigureExtractPolicyFragments(IHostApplicationBuilder builder)
    {
        ConfigureListPolicyFragments(builder);
        ConfigureWritePolicyFragmentArtifacts(builder);

        builder.Services.TryAddSingleton(GetExtractPolicyFragments);
    }

    private static ExtractPolicyFragments GetExtractPolicyFragments(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListPolicyFragments>();
        var writeArtifacts = provider.GetRequiredService<WritePolicyFragmentArtifacts>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractPolicyFragments));

            logger.LogInformation("Extracting policy fragments...");

            await list(cancellationToken)
                    .IterParallel(async resource => await writeArtifacts(resource.Name, resource.Dto, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureListPolicyFragments(IHostApplicationBuilder builder)
    {
        ConfigurationModule.ConfigureFindConfigurationNamesFactory(builder);
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListPolicyFragments);
    }

    private static ListPolicyFragments GetListPolicyFragments(IServiceProvider provider)
    {
        var findConfigurationNamesFactory = provider.GetRequiredService<FindConfigurationNamesFactory>();
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        var findConfigurationNames = findConfigurationNamesFactory.Create<PolicyFragmentName>();

        return cancellationToken =>
            findConfigurationNames()
                .Map(names => listFromSet(names, cancellationToken))
                .IfNone(() => listAll(cancellationToken));

        IAsyncEnumerable<(PolicyFragmentName, PolicyFragmentDto)> listFromSet(IEnumerable<PolicyFragmentName> names, CancellationToken cancellationToken) =>
            names.Select(name => PolicyFragmentUri.From(name, serviceUri))
                 .ToAsyncEnumerable()
                 .Choose(async uri =>
                 {
                     var dtoOption = await uri.TryGetDto(pipeline, cancellationToken);
                     return dtoOption.Map(dto => (uri.Name, dto));
                 });

        IAsyncEnumerable<(PolicyFragmentName, PolicyFragmentDto)> listAll(CancellationToken cancellationToken)
        {
            var policyFragmentsUri = PolicyFragmentsUri.From(serviceUri);
            return policyFragmentsUri.List(pipeline, cancellationToken);
        }
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