using common;
using common.tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using publisher;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace integration.tests;

public delegate ValueTask PutServiceModel(ServiceModel serviceModel, ManagementServiceName serviceName, CancellationToken cancellationToken);
public delegate ValueTask WriteServiceModelArtifacts(ServiceModel serviceModel, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);
public delegate ValueTask<ImmutableArray<CommitId>> WriteServiceModelCommits(IEnumerable<ServiceModel> serviceModels, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

internal sealed class ServiceModelModule
{
    public static void ConfigurePutServiceModel(IHostApplicationBuilder builder)
    {
        NamedValueModule.ConfigurePutNamedValueModels(builder);
        TagModule.ConfigurePutTagModels(builder);
        VersionSetModule.ConfigurePutVersionSetModels(builder);
        BackendModule.ConfigurePutBackendModels(builder);
        LoggerModule.ConfigurePutLoggerModels(builder);
        DiagnosticModule.ConfigurePutDiagnosticModels(builder);
        PolicyFragmentModule.ConfigurePutPolicyFragmentModels(builder);
        ServicePolicyModule.ConfigurePutServicePolicyModels(builder);
        GroupModule.ConfigurePutGroupModels(builder);
        ProductModule.ConfigurePutProductModels(builder);
        ApiModule.ConfigurePutApiModels(builder);
        SubscriptionModule.ConfigurePutSubscriptionModels(builder);

        builder.Services.TryAddSingleton(GetPutServiceModel);
    }

    private static PutServiceModel GetPutServiceModel(IServiceProvider serviceProvider)
    {
        var putNamedValues = serviceProvider.GetRequiredService<PutNamedValueModels>();
        var putTags = serviceProvider.GetRequiredService<PutTagModels>();
        var putVersionSets = serviceProvider.GetRequiredService<PutVersionSetModels>();
        var putBackends = serviceProvider.GetRequiredService<PutBackendModels>();
        var putLoggers = serviceProvider.GetRequiredService<PutLoggerModels>();
        var putDiagnostics = serviceProvider.GetRequiredService<PutDiagnosticModels>();
        var putPolicyFragments = serviceProvider.GetRequiredService<PutPolicyFragmentModels>();
        var putServicePolicies = serviceProvider.GetRequiredService<PutServicePolicyModels>();
        var putGroups = serviceProvider.GetRequiredService<PutGroupModels>();
        var putProducts = serviceProvider.GetRequiredService<PutProductModels>();
        var putApis = serviceProvider.GetRequiredService<PutApiModels>();
        var putSubscriptions = serviceProvider.GetRequiredService<PutSubscriptionModels>();
        var activitySource = serviceProvider.GetRequiredService<ActivitySource>();
        var logger = serviceProvider.GetRequiredService<ILogger<PutServiceModel>>();

        return async (serviceModel, serviceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutServiceModel));

            logger.LogInformation("Putting service model in APIM service {ServiceName}...", serviceName);

            await putNamedValues(serviceModel.NamedValues, serviceName, cancellationToken);
            await putTags(serviceModel.Tags, serviceName, cancellationToken);
            await putVersionSets(serviceModel.VersionSets, serviceName, cancellationToken);
            await putBackends(serviceModel.Backends, serviceName, cancellationToken);
            await putLoggers(serviceModel.Loggers, serviceName, cancellationToken);
            await putDiagnostics(serviceModel.Diagnostics, serviceName, cancellationToken);
            await putPolicyFragments(serviceModel.PolicyFragments, serviceName, cancellationToken);
            await putServicePolicies(serviceModel.ServicePolicies, serviceName, cancellationToken);
            await putGroups(serviceModel.Groups, serviceName, cancellationToken);
            await putProducts(serviceModel.Products, serviceName, cancellationToken);
            await putApis(serviceModel.Apis, serviceName, cancellationToken);
            await putSubscriptions(serviceModel.Subscriptions, serviceName, cancellationToken);
        };
    }

    public static void ConfigureWriteServiceModelArtifacts(IHostApplicationBuilder builder)
    {
        NamedValueModule.ConfigureWriteNamedValueModels(builder);
        TagModule.ConfigureWriteTagModels(builder);
        VersionSetModule.ConfigureWriteVersionSetModels(builder);
        BackendModule.ConfigureWriteBackendModels(builder);
        LoggerModule.ConfigureWriteLoggerModels(builder);
        DiagnosticModule.ConfigureWriteDiagnosticModels(builder);
        PolicyFragmentModule.ConfigureWritePolicyFragmentModels(builder);
        ServicePolicyModule.ConfigureWriteServicePolicyModels(builder);
        GroupModule.ConfigureWriteGroupModels(builder);
        ProductModule.ConfigureWriteProductModels(builder);
        ApiModule.ConfigureWriteApiModels(builder);
        SubscriptionModule.ConfigureWriteSubscriptionModels(builder);

        builder.Services.TryAddSingleton(GetWriteServiceModelArtifacts);
    }

    private static WriteServiceModelArtifacts GetWriteServiceModelArtifacts(IServiceProvider serviceProvider)
    {
        var writeNamedValues = serviceProvider.GetRequiredService<WriteNamedValueModels>();
        var writeTags = serviceProvider.GetRequiredService<WriteTagModels>();
        var writeVersionSets = serviceProvider.GetRequiredService<WriteVersionSetModels>();
        var writeBackends = serviceProvider.GetRequiredService<WriteBackendModels>();
        var writeLoggers = serviceProvider.GetRequiredService<WriteLoggerModels>();
        var writeDiagnostics = serviceProvider.GetRequiredService<WriteDiagnosticModels>();
        var writePolicyFragments = serviceProvider.GetRequiredService<WritePolicyFragmentModels>();
        var writeServicePolicies = serviceProvider.GetRequiredService<WriteServicePolicyModels>();
        var writeGroups = serviceProvider.GetRequiredService<WriteGroupModels>();
        var writeProducts = serviceProvider.GetRequiredService<WriteProductModels>();
        var writeApis = serviceProvider.GetRequiredService<WriteApiModels>();
        var writeSubscriptions = serviceProvider.GetRequiredService<WriteSubscriptionModels>();
        var activitySource = serviceProvider.GetRequiredService<ActivitySource>();
        var logger = serviceProvider.GetRequiredService<ILogger<WriteServiceModelArtifacts>>();

        return async (serviceModel, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(WriteServiceModelArtifacts));

            logger.LogInformation("Writing service model artifacts to {ServiceDirectory}...", serviceDirectory);

            await writeNamedValues(serviceModel.NamedValues, serviceDirectory, cancellationToken);
            await writeTags(serviceModel.Tags, serviceDirectory, cancellationToken);
            await writeVersionSets(serviceModel.VersionSets, serviceDirectory, cancellationToken);
            await writeBackends(serviceModel.Backends, serviceDirectory, cancellationToken);
            await writeLoggers(serviceModel.Loggers, serviceDirectory, cancellationToken);
            await writeDiagnostics(serviceModel.Diagnostics, serviceDirectory, cancellationToken);
            await writePolicyFragments(serviceModel.PolicyFragments, serviceDirectory, cancellationToken);
            await writeServicePolicies(serviceModel.ServicePolicies, serviceDirectory, cancellationToken);
            await writeGroups(serviceModel.Groups, serviceDirectory, cancellationToken);
            await writeProducts(serviceModel.Products, serviceDirectory, cancellationToken);
            await writeApis(serviceModel.Apis, serviceDirectory, cancellationToken);
            await writeSubscriptions(serviceModel.Subscriptions, serviceDirectory, cancellationToken);
        };
    }

    public static void ConfigureWriteServiceModelCommits(IHostApplicationBuilder builder)
    {
        ConfigureWriteServiceModelArtifacts(builder);

        builder.Services.TryAddSingleton(GetWriteServiceModelCommits);
    }

    private static WriteServiceModelCommits GetWriteServiceModelCommits(IServiceProvider serviceProvider)
    {
        var writeServiceModelArtifacts = serviceProvider.GetRequiredService<WriteServiceModelArtifacts>();
        var activitySource = serviceProvider.GetRequiredService<ActivitySource>();
        var logger = serviceProvider.GetRequiredService<ILogger<WriteServiceModelCommits>>();

        return async (models, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(WriteServiceModelCommits));

            logger.LogInformation("Writing service model commits to {ServiceDirectory}...", serviceDirectory);

            var authorName = "apiops";
            var authorEmail = "apiops@apiops.com";
            var repositoryDirectory = serviceDirectory.ToDirectoryInfo().Parent!;
            Git.InitializeRepository(repositoryDirectory, commitMessage: "Initial commit", authorName, authorEmail, DateTimeOffset.UtcNow);

            var commitIds = ImmutableArray<CommitId>.Empty;
            await models.Select((model, index) => (model, index))
                        .Iter(async x =>
                        {
                            var (model, index) = x;
                            DeleteNonGitDirectories(serviceDirectory);
                            await writeServiceModelArtifacts(model, serviceDirectory, cancellationToken);
                            var commit = Git.CommitChanges(repositoryDirectory, commitMessage: $"Commit {index}", authorName, authorEmail, DateTimeOffset.UtcNow);
                            var commitId = new CommitId(commit.Sha);
                            ImmutableInterlocked.Update(ref commitIds, commitIds => commitIds.Add(commitId));
                        }, cancellationToken);

            return commitIds;
        };

        static void DeleteNonGitDirectories(ManagementServiceDirectory serviceDirectory) =>
            serviceDirectory.ToDirectoryInfo()
                            .ListDirectories("*")
                            .Where(directory => directory.Name.Equals(".git", StringComparison.OrdinalIgnoreCase) is false)
                            .Iter(directory => directory.ForceDelete());
    }
}
