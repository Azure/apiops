using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using Azure.ResourceManager;
using common;
using Flurl;
using LanguageExt;
using LanguageExt.UnsafeValueAccess;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using System;
using System.Collections.Frozen;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace publisher;

/// <summary>
/// Get files that changed in the commit. If no commit ID is provided, it will return all artifact files.
/// </summary>
/// <returns></returns>
internal delegate FrozenSet<FileInfo> GetPublisherFiles();

/// <summary>
/// Lists all files in the artifact directory. If a commit ID is provided, it will list
/// the files as of that commit.
/// </summary>
internal delegate FrozenSet<FileInfo> GetArtifactFiles();

/// <summary>
/// Returns a dictionary of files in the previous commit and a function to get their contents.
/// </summary>
/// <returns></returns>
internal delegate FrozenDictionary<FileInfo, Func<CancellationToken, ValueTask<Option<BinaryData>>>> GetArtifactsInPreviousCommit();

/// <summary>
/// Gets the contents of a file. If the publisher is running in the context of a Git commit,
/// the file contents will be retrieved from the Git repository as of that commit ID.
/// Otherwise, the file contents will come from the local file system.
/// Returns None if the file does not exist in the Git commit context or on the local file system.
/// </summary>
internal delegate ValueTask<Option<BinaryData>> TryGetFileContents(FileInfo fileInfo, CancellationToken cancellationToken);

internal delegate ValueTask PublisherAction(CancellationToken cancellationToken);

file delegate Option<CommitId> TryGetCommitId();

file delegate ValueTask<Option<BinaryData>> TryGetFileContentsInCommit(FileInfo fileInfo, CommitId commitId, CancellationToken cancellationToken);

file sealed class GetPublisherFilesHandler(TryGetCommitId tryGetCommitId, ManagementServiceDirectory serviceDirectory)
{
    private readonly Lazy<FrozenSet<FileInfo>> lazy = new(() => GetPublisherFiles(tryGetCommitId, serviceDirectory));

    public FrozenSet<FileInfo> Handle() => lazy.Value;

    private static FrozenSet<FileInfo> GetPublisherFiles(TryGetCommitId tryGetCommitId, ManagementServiceDirectory serviceDirectory) =>
        tryGetCommitId()
            .Map(commitId => GetPublisherFiles(commitId, serviceDirectory))
            .IfNone(serviceDirectory.GetFilesRecursively);

    private static FrozenSet<FileInfo> GetPublisherFiles(CommitId commitId, ManagementServiceDirectory serviceDirectory) =>
        Git.GetChangedFilesInCommit(serviceDirectory.ToDirectoryInfo(), commitId);
}

file sealed class GetArtifactFilesHandler(TryGetCommitId tryGetCommitId, ManagementServiceDirectory serviceDirectory)
{
    private readonly Lazy<FrozenSet<FileInfo>> lazy = new(() => GetArtifactFiles(tryGetCommitId, serviceDirectory));

    public FrozenSet<FileInfo> Handle() => lazy.Value;

    private static FrozenSet<FileInfo> GetArtifactFiles(TryGetCommitId tryGetCommitId, ManagementServiceDirectory serviceDirectory) =>
        tryGetCommitId()
            .Map(commitId => GetArtifactFiles(commitId, serviceDirectory))
            .IfNone(serviceDirectory.GetFilesRecursively);

    private static FrozenSet<FileInfo> GetArtifactFiles(CommitId commitId, ManagementServiceDirectory serviceDirectory) =>
        Git.GetExistingFilesInCommit(serviceDirectory.ToDirectoryInfo(), commitId);
}

file sealed class GetArtifactsInPreviousCommitHandler(TryGetCommitId tryGetCommitId,
                                                      ManagementServiceDirectory serviceDirectory,
                                                      TryGetFileContentsInCommit tryGetCommitContents)
{
    private readonly Lazy<FrozenDictionary<FileInfo, Func<CancellationToken, ValueTask<Option<BinaryData>>>>> lazy = new(() => GetArtifactsInPreviousCommit(tryGetCommitId, serviceDirectory, tryGetCommitContents));

    public FrozenDictionary<FileInfo, Func<CancellationToken, ValueTask<Option<BinaryData>>>> Handle() => lazy.Value;

    private static FrozenDictionary<FileInfo, Func<CancellationToken, ValueTask<Option<BinaryData>>>> GetArtifactsInPreviousCommit(TryGetCommitId tryGetCommitId, ManagementServiceDirectory serviceDirectory, TryGetFileContentsInCommit tryGetCommitContents) =>
        tryGetCommitId()
            .Map(commitId => GetArtifactsInPreviousCommit(commitId, serviceDirectory, tryGetCommitContents))
            .IfNone(FrozenDictionary<FileInfo, Func<CancellationToken, ValueTask<Option<BinaryData>>>>.Empty);

    private static FrozenDictionary<FileInfo, Func<CancellationToken, ValueTask<Option<BinaryData>>>> GetArtifactsInPreviousCommit(CommitId commitId, ManagementServiceDirectory serviceDirectory, TryGetFileContentsInCommit tryGetCommitContents)
    {
        var serviceDirectoryInfo = serviceDirectory.ToDirectoryInfo();

        return
            Git.TryGetPreviousCommitId(serviceDirectoryInfo, commitId)
               .Map(previousCommitId => Git.GetExistingFilesInCommit(serviceDirectoryInfo, previousCommitId)
                                           .Map(file =>
                                           {
                                               var tryGetContents = (CancellationToken cancellationToken) => tryGetCommitContents(file, previousCommitId, cancellationToken);
                                               return (file, tryGetContents);
                                           })
                                           .ToFrozenDictionary())
               .IfNone(FrozenDictionary<FileInfo, Func<CancellationToken, ValueTask<Option<BinaryData>>>>.Empty);
    }
}

file sealed class TryGetFileContentsHandler(TryGetCommitId tryGetCommitId, TryGetFileContentsInCommit tryGetCommitContents)
{
    public async ValueTask<Option<BinaryData>> Handle(FileInfo fileInfo, CancellationToken cancellationToken) =>
        await tryGetCommitId()
                .BindTask(async commitId => await tryGetCommitContents(fileInfo, commitId, cancellationToken))
                .Or(async () => await TryGetFileContentsFromFileSystem(fileInfo, cancellationToken));

    private static async ValueTask<Option<BinaryData>> TryGetFileContentsFromFileSystem(FileInfo file, CancellationToken cancellationToken) =>
        file.Exists
        ? await file.ReadAsBinaryData(cancellationToken)
        : Option<BinaryData>.None;
}

file sealed class TryGetFileContentsInCommitHandler(ManagementServiceDirectory serviceDirectory)
{
    public async ValueTask<Option<BinaryData>> Handle(FileInfo fileInfo, CommitId commitId, CancellationToken cancellationToken) =>
        await Git.TryGetFileContentsInCommit(serviceDirectory.ToDirectoryInfo(), fileInfo, commitId)
                 .MapTask(async stream =>
                 {
                     using (stream)
                     {
                         return await BinaryData.FromStreamAsync(stream, cancellationToken);
                     }
                 });
}

file sealed class TryGetCommitIdHandler(IConfiguration configuration)
{
    public Option<CommitId> Handle() =>
        configuration.TryGetValue("COMMIT_ID")
                     .Map(commitId => new CommitId(commitId));

}

internal static class CommonServices
{
    public static void Configure(IServiceCollection services)
    {
        services.AddSingleton(GetActivitySource);
        services.TryAddSingleton(GetAzureEnvironment);
        services.TryAddSingleton(GetTokenCredential);
        services.TryAddSingleton(GetConfigurationJson);
        services.TryAddSingleton(GetHttpPipeline);
        services.TryAddSingleton(GetManagementServiceName);
        services.TryAddSingleton(GetManagementServiceUri);
        services.TryAddSingleton(GetManagementServiceDirectory);
        services.TryAddSingleton<OverrideDtoFactory>();

        ConfigureGetPublisherFiles(services);
        ConfigureGetArtifactFiles(services);
        ConfigureTryGetFileContents(services);
        ConfigureGetArtifactsInPreviousCommit(services);

        services.ConfigureApimHttpClient();
        OpenTelemetryServices.Configure(services);
    }

    private static ActivitySource GetActivitySource(IServiceProvider provider) =>
        new("ApiOps.Publisher");

    private static AzureEnvironment GetAzureEnvironment(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();

        return configuration.TryGetValue("AZURE_CLOUD_ENVIRONMENT").ValueUnsafe() switch
        {
            null => AzureEnvironment.Public,
            "AzureGlobalCloud" or nameof(ArmEnvironment.AzurePublicCloud) => AzureEnvironment.Public,
            "AzureChinaCloud" or nameof(ArmEnvironment.AzureChina) => AzureEnvironment.China,
            "AzureUSGovernment" or nameof(ArmEnvironment.AzureGovernment) => AzureEnvironment.USGovernment,
            "AzureGermanCloud" or nameof(ArmEnvironment.AzureGermany) => AzureEnvironment.Germany,
            _ => throw new InvalidOperationException($"AZURE_CLOUD_ENVIRONMENT is invalid. Valid values are {nameof(ArmEnvironment.AzurePublicCloud)}, {nameof(ArmEnvironment.AzureChina)}, {nameof(ArmEnvironment.AzureGovernment)}, {nameof(ArmEnvironment.AzureGermany)}")
        };
    }

    private static TokenCredential GetTokenCredential(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();
        var azureAuthorityHost = provider.GetRequiredService<AzureEnvironment>().AuthorityHost;

        return configuration.TryGetValue("AZURE_BEARER_TOKEN")
                            .Map(GetCredentialFromToken)
                            .IfNone(() => GetDefaultAzureCredential(azureAuthorityHost));
    }

    private static TokenCredential GetCredentialFromToken(string token)
    {
        var jsonWebToken = new JsonWebToken(token);
        var expirationDate = new DateTimeOffset(jsonWebToken.ValidTo);
        var accessToken = new AccessToken(token, expirationDate);

        return DelegatedTokenCredential.Create((context, cancellationToken) => accessToken);
    }

    private static DefaultAzureCredential GetDefaultAzureCredential(Uri azureAuthorityHost) =>
        new(new DefaultAzureCredentialOptions
        {
            AuthorityHost = azureAuthorityHost
        });

    private static ConfigurationJson GetConfigurationJson(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();
        var configurationJson = ConfigurationJson.From(configuration);

        return GetConfigurationJsonFromYaml(configuration)
                .Map(configurationJson.MergeWith)
                .IfNone(configurationJson);
    }

    private static Option<ConfigurationJson> GetConfigurationJsonFromYaml(IConfiguration configuration) =>
        configuration.TryGetValue("CONFIGURATION_YAML_PATH")
                     .Map(path => new FileInfo(path))
                     .Where(file => file.Exists)
                     .Map(file =>
                     {
                         using var reader = File.OpenText(file.FullName);
                         return ConfigurationJson.FromYaml(reader);
                     });

    private static HttpPipeline GetHttpPipeline(IServiceProvider provider)
    {
        var clientOptions = ClientOptions.Default;
        clientOptions.RetryPolicy = new CommonRetryPolicy();

        var tokenCredential = provider.GetRequiredService<TokenCredential>();
        var azureEnvironment = provider.GetRequiredService<AzureEnvironment>();
        var bearerAuthenticationPolicy = new BearerTokenAuthenticationPolicy(tokenCredential, azureEnvironment.DefaultScope);

        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(HttpPipeline));
        var loggingPolicy = new ILoggerHttpPipelinePolicy(logger);

        var version = Assembly.GetExecutingAssembly()?.GetName().Version ?? new Version("-1");
        var telemetryPolicy = new TelemetryPolicy(version);

        return HttpPipelineBuilder.Build(clientOptions, bearerAuthenticationPolicy, loggingPolicy, telemetryPolicy);
    }

    private static ManagementServiceName GetManagementServiceName(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();

        var name = configuration.TryGetValue("API_MANAGEMENT_SERVICE_NAME")
                                .IfNone(() => configuration.GetValue("apimServiceName"));

        return ManagementServiceName.From(name);
    }

    private static ManagementServiceUri GetManagementServiceUri(IServiceProvider provider)
    {
        var azureEnvironment = provider.GetRequiredService<AzureEnvironment>();
        var serviceName = provider.GetRequiredService<ManagementServiceName>();
        var configuration = provider.GetRequiredService<IConfiguration>();

        var apiVersion = configuration.TryGetValue("ARM_API_VERSION")
                                      .IfNone(() => "2022-08-01");

        var uri = azureEnvironment.ManagementEndpoint
                                  .AppendPathSegment("subscriptions")
                                  .AppendPathSegment(configuration.GetValue("AZURE_SUBSCRIPTION_ID"))
                                  .AppendPathSegment("resourceGroups")
                                  .AppendPathSegment(configuration.GetValue("AZURE_RESOURCE_GROUP_NAME"))
                                  .AppendPathSegment("providers/Microsoft.ApiManagement/service")
                                  .AppendPathSegment(serviceName.ToString())
                                  .SetQueryParam("api-version", apiVersion)
                                  .ToUri();

        return ManagementServiceUri.From(uri);
    }

    private static ManagementServiceDirectory GetManagementServiceDirectory(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();
        var directoryPath = configuration.GetValue("API_MANAGEMENT_SERVICE_OUTPUT_FOLDER_PATH");
        var directory = new DirectoryInfo(directoryPath);

        return ManagementServiceDirectory.From(directory);
    }

    private static void ConfigureGetPublisherFiles(IServiceCollection services)
    {
        ConfigureTryGetCommitId(services);

        services.TryAddSingleton<GetPublisherFilesHandler>();
        services.TryAddSingleton<GetPublisherFiles>(provider => provider.GetRequiredService<GetPublisherFilesHandler>().Handle);
    }

    private static void ConfigureTryGetCommitId(IServiceCollection services)
    {
        services.TryAddSingleton<TryGetCommitIdHandler>();
        services.TryAddSingleton<TryGetCommitId>(provider => provider.GetRequiredService<TryGetCommitIdHandler>().Handle);
    }

    private static void ConfigureGetArtifactFiles(IServiceCollection services)
    {
        ConfigureTryGetCommitId(services);

        services.TryAddSingleton<GetArtifactFilesHandler>();
        services.TryAddSingleton<GetArtifactFiles>(provider => provider.GetRequiredService<GetArtifactFilesHandler>().Handle);
    }

    private static void ConfigureTryGetFileContents(IServiceCollection services)
    {
        ConfigureTryGetCommitId(services);
        ConfigureTryGetFileContentsInCommit(services);

        services.TryAddSingleton<TryGetFileContentsHandler>();
        services.TryAddSingleton<TryGetFileContents>(provider => provider.GetRequiredService<TryGetFileContentsHandler>().Handle);
    }

    private static void ConfigureTryGetFileContentsInCommit(IServiceCollection services)
    {
        services.TryAddSingleton<TryGetFileContentsInCommitHandler>();
        services.TryAddSingleton<TryGetFileContentsInCommit>(provider => provider.GetRequiredService<TryGetFileContentsInCommitHandler>().Handle);
    }

    private static void ConfigureGetArtifactsInPreviousCommit(IServiceCollection services)
    {
        ConfigureTryGetCommitId(services);
        ConfigureTryGetFileContentsInCommit(services);

        services.TryAddSingleton<GetArtifactsInPreviousCommitHandler>();
        services.TryAddSingleton<GetArtifactsInPreviousCommit>(provider => provider.GetRequiredService<GetArtifactsInPreviousCommitHandler>().Handle);
    }
}

file static class Common
{
    public static FrozenSet<FileInfo> GetFilesRecursively(this ManagementServiceDirectory serviceDirectory) =>
        serviceDirectory.ToDirectoryInfo()
                        .EnumerateFiles("*", SearchOption.AllDirectories)
                        .ToFrozenSet(x => x.FullName);
}