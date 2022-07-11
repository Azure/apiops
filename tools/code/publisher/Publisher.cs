using common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MoreLinq;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace publisher;

internal class Publisher : BackgroundService
{
    private readonly IHostApplicationLifetime applicationLifetime;
    private readonly ILogger logger;
    private readonly ConfigurationModel configurationModel;
    private readonly Func<Uri, CancellationToken, IAsyncEnumerable<JsonObject>> getResources;
    private readonly Func<Uri, JsonObject, CancellationToken, ValueTask> putResource;
    private readonly Func<Uri, CancellationToken, ValueTask> deleteResource;
    private readonly ServiceDirectory serviceDirectory;
    private readonly ServiceProviderUri serviceProviderUri;
    private readonly ServiceName serviceName;
    private readonly CommitId? commitId;
    private readonly FileInfo? configurationFile;

    public Publisher(IHostApplicationLifetime applicationLifetime, ILogger<Publisher> logger, IConfiguration configuration, AzureHttpClient azureHttpClient)
    {
        this.applicationLifetime = applicationLifetime;
        this.logger = logger;
        this.getResources = azureHttpClient.GetResourcesAsJsonObjects;
        this.putResource = azureHttpClient.PutJsonObject;
        this.deleteResource = azureHttpClient.DeleteResource;
        this.serviceDirectory = GetServiceDirectory(configuration);
        this.configurationModel = configuration.Get<ConfigurationModel>();
        this.serviceProviderUri = GetServiceProviderUri(configuration, azureHttpClient);
        this.serviceName = GetServiceName(configuration);
        this.commitId = TryGetCommitId(configuration);
        this.configurationFile = TryGetConfigurationFile(configuration);
    }

    private static ServiceDirectory GetServiceDirectory(IConfiguration configuration) =>
        ServiceDirectory.From(configuration.GetValue("API_MANAGEMENT_SERVICE_OUTPUT_FOLDER_PATH"));

    private static ServiceProviderUri GetServiceProviderUri(IConfiguration configuration, AzureHttpClient azureHttpClient)
    {
        var subscriptionId = configuration.GetValue("AZURE_SUBSCRIPTION_ID");
        var resourceGroupName = configuration.GetValue("AZURE_RESOURCE_GROUP_NAME");

        return ServiceProviderUri.From(azureHttpClient.ResourceManagerEndpoint, subscriptionId, resourceGroupName);
    }

    private static ServiceName GetServiceName(IConfiguration configuration)
    {
        var serviceName = configuration.TryGetValue("apimServiceName") ?? configuration.TryGetValue("API_MANAGEMENT_SERVICE_NAME");

        return ServiceName.From(serviceName ?? throw new InvalidOperationException("Could not find service name in configuration. Either specify it in key 'apimServiceName' or 'API_MANAGEMENT_SERVICE_NAME'."));
    }

    private static CommitId? TryGetCommitId(IConfiguration configuration)
    {
        var commitId = configuration.TryGetValue("COMMIT_ID");

        return commitId is null ? null : CommitId.From(commitId);
    }

    private static FileInfo? TryGetConfigurationFile(IConfiguration configuration)
    {
        var filePath = configuration.TryGetValue("CONFIGURATION_YAML_PATH");

        return filePath is null ? null : new FileInfo(filePath);
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        try
        {
            logger.LogInformation("Beginning execution...");

            await Run(cancellationToken);

            logger.LogInformation("Execution complete.");
        }
        catch (OperationCanceledException)
        {
            // Don't throw if operation was canceled
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "");
            Environment.ExitCode = -1;
            throw;
        }
        finally
        {
            applicationLifetime.StopApplication();
        }
    }

    private async ValueTask Run(CancellationToken cancellationToken)
    {
        logger.LogInformation("Getting files to process...");
        var dictionary = await GetFilesToProcess(cancellationToken);

        if (dictionary.TryGetValue(Action.Delete, out var filesToDelete))
        {
            logger.LogInformation("Deleting files...");
            await DeleteFiles(filesToDelete, cancellationToken);
        }

        if (dictionary.TryGetValue(Action.Put, out var filesToPut))
        {
            logger.LogInformation("Putting files...");
            await PutFiles(filesToPut, cancellationToken);
        }
    }

    private async Task<ImmutableDictionary<Action, ImmutableList<FileRecord>>> GetFilesToProcess(CancellationToken cancellationToken)
    {
        if (commitId is null)
        {
            logger.LogInformation("Commit ID was not specified, getting all files from {serviceDirectory}...", serviceDirectory.Path);
            var fileRecords = GetFileRecordsFromServiceDirectory().ToImmutableList();
            return Enumerable.Repeat(fileRecords, 1).ToImmutableDictionary(_ => Action.Put);
        }
        else
        {
            logger.LogInformation("Getting files from commit ID {commitId}...", commitId);
            var commitIdFileRecords = await GetFileRecordsFromCommitId(commitId, cancellationToken);
            var configurationFileModified = await WasConfigurationFileChangedInCommitId(commitId, cancellationToken);

            if (configurationFileModified)
            {
                logger.LogInformation("Configuration file was modified in commit ID, will include its contents.");

                var configurationFileRecords = GetFileRecordsFromServiceDirectory()
                                                .Where(IsFileRecordInConfiguration)
                                                .ToImmutableList();

                return configurationFileRecords.Any()
                        // If the commit included artifacts to put, merge them with configuration records.
                        ? commitIdFileRecords.SetItem(Action.Put,
                                                      commitIdFileRecords.TryGetValue(Action.Put, out var existingCommitIdArtifacts)
                                                      ? existingCommitIdArtifacts.Union(configurationFileRecords).ToImmutableList()
                                                      : configurationFileRecords)
                        : commitIdFileRecords;
            }
            else
            {
                return commitIdFileRecords;
            }
        }
    }

    private IEnumerable<FileRecord> GetFileRecordsFromServiceDirectory()
    {
        return ((DirectoryInfo)serviceDirectory).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true })
                                                .Choose(TryClassifyFile);
    }

    private async ValueTask<ImmutableDictionary<Action, ImmutableList<FileRecord>>> GetFileRecordsFromCommitId(CommitId commitId, CancellationToken cancellationToken)
    {
        var files =
            from grouping in Git.GetFilesFromCommit(commitId, serviceDirectory)
            let action = grouping.Key == CommitStatus.Delete ? Action.Delete : Action.Put
            let fileRecords = grouping.Choose(TryClassifyFile)
            select (action, fileRecords);

        var fileList = await files.ToListAsync(cancellationToken);

        return fileList.ToImmutableDictionary(pair => pair.action, pair => pair.fileRecords.ToImmutableList());
    }

    private async Task<bool> WasConfigurationFileChangedInCommitId(CommitId commitId, CancellationToken cancellationToken)
    {
        if (configurationFile is null)
        {
            return false;
        }

        var configurationDirectory = configurationFile.Directory;
        if (configurationDirectory is null)
        {
            return false;
        }

        return await Git.GetFilesFromCommit(commitId, configurationDirectory)
                        .Where(grouping => grouping.Key != CommitStatus.Delete)
                        .Where(grouping => grouping.Any(file => file.FullName.Equals(configurationFile.FullName)))
                        .AnyAsync(cancellationToken);
    }

    private bool IsFileRecordInConfiguration(FileRecord fileRecord)
    {
        switch (fileRecord)
        {
            case NamedValueInformationFile file:
                var namedValueName = NamedValue.GetNameFromFile(file).ToString();
                return configurationModel.NamedValues?.Any(value => value.Name?.Equals(namedValueName) ?? false) ?? false;
            case GatewayInformationFile file:
                var gatewayName = Gateway.GetNameFromFile(file).ToString();
                return configurationModel.Gateways?.Any(value => value.Name?.Equals(gatewayName) ?? false) ?? false;
            case LoggerInformationFile file:
                var loggerName = Logger.GetNameFromFile(file).ToString();
                return configurationModel.Loggers?.Any(value => value.Name?.Equals(loggerName) ?? false) ?? false;
            case ProductInformationFile file:
                var productName = Product.GetNameFromFile(file).ToString();
                return configurationModel.Products?.Any(value => value.Name?.Equals(productName) ?? false) ?? false;
            case DiagnosticInformationFile file:
                var diagnosticName = Diagnostic.GetNameFromFile(file).ToString();
                return configurationModel.Diagnostics?.Any(value => value.Name?.Equals(diagnosticName) ?? false) ?? false;
            case ApiInformationFile file:
                var apiName = Api.GetNameFromFile(file).ToString();
                return configurationModel.Apis?.Any(value => value.Name?.Equals(apiName) ?? false) ?? false;
            case ApiDiagnosticInformationFile file:
                var apiDiagnosticName = ApiDiagnostic.GetNameFromFile(file).ToString();
                var apiInformationFile = ApiInformationFile.From(file.ApiDiagnosticDirectory.ApiDiagnosticsDirectory.ApiDirectory);
                var apiDiagnosticApiName = Api.GetNameFromFile(apiInformationFile).ToString();
                return configurationModel.Apis?.Any(value => (value.Name?.Equals(apiDiagnosticApiName) ?? false)
                                                             && (value.Diagnostics?.Any(value => value.Name?.Equals(apiDiagnosticName) ?? false) ?? false)) ?? false;
            default:
                return false;
        }
    }

    private async ValueTask DeleteFiles(IReadOnlyCollection<FileRecord> files, CancellationToken cancellationToken)
    {
        var servicePolicyFiles = files.Choose(file => file as ServicePolicyFile).ToList();
        var gatewayInformationFiles = files.Choose(file => file as GatewayInformationFile).ToList();
        var namedValueInformationFiles = files.Choose(file => file as NamedValueInformationFile).ToList();
        var loggerInformationFiles = files.Choose(file => file as LoggerInformationFile).ToList();
        var gatewayApisFiles = files.Choose(file => file as GatewayApisFile).ToList();
        var productInformationFiles = files.Choose(file => file as ProductInformationFile).ToList();
        var productPolicyFiles = files.Choose(file => file as ProductPolicyFile).ToList();
        var productApisFiles = files.Choose(file => file as ProductApisFile).ToList();
        var diagnosticInformationFiles = files.Choose(file => file as DiagnosticInformationFile).ToList();
        var apiVersionSetInformationFiles = files.Choose(file => file as ApiVersionSetInformationFile).ToList();
        var apiInformationFiles = files.Choose(file => file as ApiInformationFile).ToList();
        var apiDiagnosticInformationFiles = files.Choose(file => file as ApiDiagnosticInformationFile).ToList();
        var apiPolicyFiles = files.Choose(file => file as ApiPolicyFile).ToList();
        var apiOperationPolicyFiles = files.Choose(file => file as ApiOperationPolicyFile).ToList();

        await DeleteApiOperationPolicies(apiOperationPolicyFiles, cancellationToken);
        await DeleteApiDiagnostics(apiDiagnosticInformationFiles, cancellationToken);
        await DeleteApiPolicies(apiPolicyFiles, cancellationToken);
        await DeleteApis(apiInformationFiles, cancellationToken);
        await DeleteApiVersionSets(apiVersionSetInformationFiles, cancellationToken);
        await DeleteLoggers(loggerInformationFiles, cancellationToken);
        await DeleteGatewayApis(gatewayApisFiles, cancellationToken);
        await DeleteGateways(gatewayInformationFiles, cancellationToken);
        await DeleteProductApis(productApisFiles, cancellationToken);
        await DeleteProductPolicies(productPolicyFiles, cancellationToken);
        await DeleteProducts(productInformationFiles, cancellationToken);
        await DeleteDiagnostics(diagnosticInformationFiles, cancellationToken);
        await DeleteNamedValues(namedValueInformationFiles, cancellationToken);
        await DeleteServicePolicy(servicePolicyFiles, cancellationToken);
    }

    private async ValueTask PutFiles(IReadOnlyCollection<FileRecord> files, CancellationToken cancellationToken)
    {
        var servicePolicyFiles = files.Choose(file => file as ServicePolicyFile).ToList();
        var gatewayInformationFiles = files.Choose(file => file as GatewayInformationFile).ToList();
        var namedValueInformationFiles = files.Choose(file => file as NamedValueInformationFile).ToList();
        var loggerInformationFiles = files.Choose(file => file as LoggerInformationFile).ToList();
        var productInformationFiles = files.Choose(file => file as ProductInformationFile).ToList();
        var productPolicyFiles = files.Choose(file => file as ProductPolicyFile).ToList();
        var gatewayApisFiles = files.Choose(file => file as GatewayApisFile).ToList();
        var productApisFiles = files.Choose(file => file as ProductApisFile).ToList();
        var diagnosticInformationFiles = files.Choose(file => file as DiagnosticInformationFile).ToList();
        var apiVersionSetInformationFiles = files.Choose(file => file as ApiVersionSetInformationFile).ToList();
        var apiInformationFiles = files.Choose(file => file as ApiInformationFile).ToList();
        var apiSpecificationFiles = files.Choose(file => file as ApiSpecificationFile).ToList();
        var apiDiagnosticInformationFiles = files.Choose(file => file as ApiDiagnosticInformationFile).ToList();
        var apiPolicyFiles = files.Choose(file => file as ApiPolicyFile).ToList();
        var apiOperationPolicyFiles = files.Choose(file => file as ApiOperationPolicyFile).ToList();

        await PutNamedValueInformationFiles(namedValueInformationFiles, cancellationToken);
        await PutServicePolicyFile(servicePolicyFiles, cancellationToken);
        await PutLoggerInformationFiles(loggerInformationFiles, cancellationToken);
        await PutDiagnosticInformationFiles(diagnosticInformationFiles, cancellationToken);
        await PutGatewayInformationFiles(gatewayInformationFiles, cancellationToken);
        await PutProductInformationFiles(productInformationFiles, cancellationToken);
        await PutProductPolicyFiles(productPolicyFiles, cancellationToken);
        await PutApiVersionSetInformationFiles(apiVersionSetInformationFiles, cancellationToken);
        await PutApiInformationAndSpecificationFiles(apiInformationFiles, apiSpecificationFiles, cancellationToken);
        await PutApiPolicyFiles(apiPolicyFiles, cancellationToken);
        await PutApiDiagnosticInformationFiles(apiDiagnosticInformationFiles, cancellationToken);
        await PutApiOperationPolicyFiles(apiOperationPolicyFiles, cancellationToken);
        await PutGatewayApisFiles(gatewayApisFiles, cancellationToken);
        await PutProductApisFiles(productApisFiles, cancellationToken);
    }

    private FileRecord? TryClassifyFile(FileInfo file) =>
        GatewayInformationFile.TryFrom(serviceDirectory, file) as FileRecord
        ?? LoggerInformationFile.TryFrom(serviceDirectory, file) as FileRecord
        ?? ServicePolicyFile.TryFrom(serviceDirectory, file) as FileRecord
        ?? NamedValueInformationFile.TryFrom(serviceDirectory, file) as FileRecord
        ?? ProductInformationFile.TryFrom(serviceDirectory, file) as FileRecord
        ?? GatewayApisFile.TryFrom(serviceDirectory, file) as FileRecord
        ?? ProductPolicyFile.TryFrom(serviceDirectory, file) as FileRecord
        ?? ProductApisFile.TryFrom(serviceDirectory, file) as FileRecord
        ?? DiagnosticInformationFile.TryFrom(serviceDirectory, file) as FileRecord
        ?? ApiVersionSetInformationFile.TryFrom(serviceDirectory, file) as FileRecord
        ?? ApiInformationFile.TryFrom(serviceDirectory, file) as FileRecord
        ?? ApiSpecificationFile.TryFrom(serviceDirectory, file) as FileRecord
        ?? ApiDiagnosticInformationFile.TryFrom(serviceDirectory, file) as FileRecord
        ?? ApiPolicyFile.TryFrom(serviceDirectory, file) as FileRecord
        ?? ApiOperationPolicyFile.TryFrom(serviceDirectory, file) as FileRecord;

    private async ValueTask PutServicePolicyFile(IReadOnlyCollection<ServicePolicyFile> files, CancellationToken cancellationToken)
    {
        var servicePolicyFile = files.SingleOrDefault();

        if (servicePolicyFile is not null)
        {
            await PutServicePolicyFile(servicePolicyFile, cancellationToken);
        }
    }

    private async ValueTask PutServicePolicyFile(ServicePolicyFile file, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting service policy file {servicePolicyFile}...", file.Path);

        var policyText = await file.ReadAsText(cancellationToken);
        await ServicePolicy.Put(putResource, serviceProviderUri, serviceName, policyText, cancellationToken);
    }

    private async ValueTask DeleteServicePolicy(IReadOnlyCollection<ServicePolicyFile> files, CancellationToken cancellationToken)
    {
        var servicePolicyFile = files.SingleOrDefault();

        if (servicePolicyFile is not null)
        {
            await DeleteServicePolicy(servicePolicyFile, cancellationToken);
        }
    }

    private async ValueTask DeleteServicePolicy(ServicePolicyFile file, CancellationToken cancellationToken)
    {
        logger.LogInformation("File {servicePolicyFile} was removed, deleting service policy...", file.Path);

        await ServicePolicy.Delete(deleteResource, serviceProviderUri, serviceName, cancellationToken);
    }

    private async ValueTask PutGatewayInformationFiles(IReadOnlyCollection<GatewayInformationFile> files, CancellationToken cancellationToken)
    {
        await Parallel.ForEachAsync(files, cancellationToken, PutGatewayInformationFile);
    }

    private async ValueTask PutGatewayInformationFile(GatewayInformationFile file, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting gateway information file {gatewayInformationFile}...", file.Path);

        var json = file.ReadAsJsonObject();
        var gateway = Gateway.Deserialize(json);

        var configurationGateway = configurationModel?.Gateways?.FirstOrDefault(configurationGateway => configurationGateway.Name == gateway.Name);
        if (configurationGateway is not null)
        {
            logger.LogInformation("Found gateway {gateway} in configuration...", gateway.Name);
            gateway = gateway with
            {
                Properties = gateway.Properties with
                {
                    Description = configurationGateway.Description ?? gateway.Properties.Description
                }
            };
        }

        await Gateway.Put(putResource, serviceProviderUri, serviceName, gateway, cancellationToken);
    }

    private async ValueTask DeleteGateways(IReadOnlyCollection<GatewayInformationFile> files, CancellationToken cancellationToken)
    {
        await Parallel.ForEachAsync(files, cancellationToken, DeleteGateway);
    }

    private async ValueTask DeleteGateway(GatewayInformationFile file, CancellationToken cancellationToken)
    {
        logger.LogInformation("File {gatewayInformationFile} was removed, deleting gateway...", file.Path);

        if (commitId is null)
        {
            throw new InvalidOperationException("Commit ID is null. We need it to get the deleted file contents.");
        }

        var fileText = await Git.GetPreviousCommitContents(commitId, file, serviceDirectory);
        var json = JsonNode.Parse(fileText)?.AsObject() ?? throw new InvalidOperationException("Could not deserialize file contents to JSON.");
        var name = GatewayName.From(Gateway.Deserialize(json).Name);

        await Gateway.Delete(deleteResource, serviceProviderUri, serviceName, name, cancellationToken);
    }

    private async ValueTask DeleteApiVersionSets(IReadOnlyCollection<ApiVersionSetInformationFile> files, CancellationToken cancellationToken)
    {
        await Parallel.ForEachAsync(files, cancellationToken, DeleteApiVersionSet);
    }

    private async ValueTask DeleteApiVersionSet(ApiVersionSetInformationFile file, CancellationToken cancellationToken)
    {
        logger.LogInformation("File {apiVersionSetInformationFile} was removed, deleting api version set...", file.Path);

        if (commitId is null)
        {
            throw new InvalidOperationException("Commit ID is null. We need it to get the deleted file contents.");
        }

        var fileText = await Git.GetPreviousCommitContents(commitId, file, serviceDirectory);
        var json = JsonNode.Parse(fileText)?.AsObject() ?? throw new InvalidOperationException("Could not deserialize file contents to JSON.");
        var id = ApiVersionSetId.From(ApiVersionSet.Deserialize(json).Name);

        await ApiVersionSet.Delete(deleteResource, serviceProviderUri, serviceName, id, cancellationToken);
    }

    private async ValueTask PutGatewayApisFiles(IReadOnlyCollection<GatewayApisFile> files, CancellationToken cancellationToken)
    {
        await Parallel.ForEachAsync(files, cancellationToken, PutGatewayApisFile);
    }

    private async ValueTask PutGatewayApisFile(GatewayApisFile file, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting gateway apis file {gatewayApisFile}...", file.Path);

        var gatewayInformationFile = GatewayInformationFile.From(file.GatewayDirectory);
        if (gatewayInformationFile.Exists() is false)
        {
            throw new InvalidOperationException($"Gateway information file is missing. Expected path is {gatewayInformationFile.Path}. Cannot put gateway APIs file {file.Path}.");
        }

        var gatewayName = Gateway.GetNameFromFile(gatewayInformationFile);
        var fileApiNames = GatewayApi.ListFromFile(file).ToAsyncEnumerable();
        var existingApiNames = GatewayApi.List(getResources, serviceProviderUri, serviceName, gatewayName, cancellationToken).Select(api => ApiName.From(api.Name));

        var apiNamesToAdd = fileApiNames.Except(existingApiNames);
        var apiNamesToRemove = existingApiNames.Except(fileApiNames);

        await Parallel.ForEachAsync(apiNamesToAdd, cancellationToken, (apiName, cancellationToken) =>
        {
            logger.LogInformation("Adding gateway {gatewayName} api {apiName}...", gatewayName, apiName);
            return GatewayApi.Put(putResource, serviceProviderUri, serviceName, gatewayName, apiName, cancellationToken);
        });

        await Parallel.ForEachAsync(apiNamesToRemove, cancellationToken, (apiName, cancellationToken) =>
        {
            logger.LogInformation("Removing gateway {gatewayName} api {apiName}...", gatewayName, apiName);
            return GatewayApi.Delete(deleteResource, serviceProviderUri, serviceName, gatewayName, apiName, cancellationToken);
        });
    }

    private async ValueTask DeleteGatewayApis(IReadOnlyCollection<GatewayApisFile> files, CancellationToken cancellationToken)
    {
        await Parallel.ForEachAsync(files, cancellationToken, DeleteGatewayApis);
    }

    private async ValueTask DeleteGatewayApis(GatewayApisFile file, CancellationToken cancellationToken)
    {
        logger.LogInformation("File {gatewayApisFile} was removed, deleting gateway APIs...", file.Path);

        var gatewayInformationFile = GatewayInformationFile.From(file.GatewayDirectory);
        if (gatewayInformationFile.Exists() is false)
        {
            logger.LogWarning("Gateway information file {gatewayInformationFile} is missing. Cannot get gateway for {gatewayApisFile}.", gatewayInformationFile.Path, file.Path);
            return;
        }

        var gatewayName = Gateway.GetNameFromFile(gatewayInformationFile);
        var gatewayApis = GatewayApi.List(getResources, serviceProviderUri, serviceName, gatewayName, cancellationToken);
        await Parallel.ForEachAsync(gatewayApis, cancellationToken, (gatewayApi, cancellationToken) =>
        {
            var apiName = ApiName.From(gatewayApi.Name);
            return GatewayApi.Delete(deleteResource, serviceProviderUri, serviceName, gatewayName, apiName, cancellationToken);
        });
    }

    private async ValueTask PutLoggerInformationFiles(IReadOnlyCollection<LoggerInformationFile> files, CancellationToken cancellationToken)
    {
        await Parallel.ForEachAsync(files, cancellationToken, PutLoggerInformationFile);
    }

    private async ValueTask PutLoggerInformationFile(LoggerInformationFile file, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting logger information file {loggerInformationFile}...", file.Path);

        var json = file.ReadAsJsonObject();
        var loggerModel = Logger.Deserialize(json);

        var configurationLogger = configurationModel?.Loggers?.FirstOrDefault(configurationLogger => configurationLogger.Name == loggerModel.Name);
        if (configurationLogger is not null)
        {
            logger.LogInformation("Found logger {logger} in configuration...", loggerModel.Name);
            loggerModel = loggerModel with
            {
                Properties = loggerModel.Properties with
                {
                    Credentials = configurationLogger.Credentials is null
                                  ? loggerModel.Properties.Credentials
                                  : loggerModel.Properties.Credentials is null
                                    ? new common.Models.Logger.Credentials
                                    {
                                        Name = configurationLogger.Credentials.Name ?? loggerModel.Properties.Credentials?.Name,
                                        ConnectionString = configurationLogger.Credentials.ConnectionString ?? loggerModel.Properties.Credentials?.ConnectionString,
                                        InstrumentationKey = configurationLogger.Credentials.InstrumentationKey ?? loggerModel.Properties.Credentials?.InstrumentationKey
                                    }
                                    : loggerModel.Properties.Credentials with
                                    {
                                        Name = configurationLogger.Credentials.Name ?? loggerModel.Properties.Credentials?.Name,
                                        ConnectionString = configurationLogger.Credentials.ConnectionString ?? loggerModel.Properties.Credentials?.ConnectionString,
                                        InstrumentationKey = configurationLogger.Credentials.InstrumentationKey ?? loggerModel.Properties.Credentials?.InstrumentationKey
                                    },
                    Description = configurationLogger.Description ?? loggerModel.Properties.Description,
                    IsBuffered = configurationLogger.IsBuffered ?? loggerModel.Properties.IsBuffered,
                    LoggerType = configurationLogger.LoggerType ?? loggerModel.Properties.LoggerType,
                    ResourceId = configurationLogger.ResourceId ?? loggerModel.Properties.ResourceId
                }
            };
        }

        await Logger.Put(putResource, serviceProviderUri, serviceName, loggerModel, cancellationToken);
    }

    private async ValueTask DeleteLoggers(IReadOnlyCollection<LoggerInformationFile> files, CancellationToken cancellationToken)
    {
        await Parallel.ForEachAsync(files, cancellationToken, DeleteLogger);
    }

    private async ValueTask DeleteLogger(LoggerInformationFile file, CancellationToken cancellationToken)
    {
        logger.LogInformation("File {loggerInformationFile} was removed, deleting logger...", file.Path);

        if (commitId is null)
        {
            throw new InvalidOperationException("Commit ID is null. We need it to get the deleted file contents.");
        }

        var fileText = await Git.GetPreviousCommitContents(commitId, file, serviceDirectory);
        var json = JsonNode.Parse(fileText)?.AsObject() ?? throw new InvalidOperationException("Could not deserialize file contents to JSON.");
        var name = LoggerName.From(Logger.Deserialize(json).Name);

        await Logger.Delete(deleteResource, serviceProviderUri, serviceName, name, cancellationToken);
    }

    private async ValueTask PutDiagnosticInformationFiles(IReadOnlyCollection<DiagnosticInformationFile> files, CancellationToken cancellationToken)
    {
        await Parallel.ForEachAsync(files, cancellationToken, PutDiagnosticInformationFile);
    }

    private async ValueTask PutDiagnosticInformationFile(DiagnosticInformationFile file, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting diagnostic information file {diagnosticInformationFile}...", file.Path);

        var json = file.ReadAsJsonObject();
        var diagnostic = Diagnostic.Deserialize(json);

        var configurationDiagnostic = configurationModel?.Diagnostics?.FirstOrDefault(configurationDiagnostic => configurationDiagnostic.Name == diagnostic.Name);
        if (configurationDiagnostic is not null)
        {
            logger.LogInformation("Found diagnostic {diagnostic} in configuration...", diagnostic.Name);
            diagnostic = diagnostic with
            {
                Properties = diagnostic.Properties with
                {
                    LoggerId = configurationDiagnostic.LoggerId ?? diagnostic.Properties.LoggerId,
                    Verbosity = configurationDiagnostic.Verbosity ?? diagnostic.Properties.Verbosity
                }
            };
        }

        await Diagnostic.Put(putResource, serviceProviderUri, serviceName, diagnostic, cancellationToken);
    }

    private async ValueTask DeleteDiagnostics(IReadOnlyCollection<DiagnosticInformationFile> files, CancellationToken cancellationToken)
    {
        await Parallel.ForEachAsync(files, cancellationToken, DeleteDiagnostic);
    }

    private async ValueTask DeleteDiagnostic(DiagnosticInformationFile file, CancellationToken cancellationToken)
    {
        logger.LogInformation("File {diagnosticInformationFile} was removed, deleting diagnostic...", file.Path);

        if (commitId is null)
        {
            throw new InvalidOperationException("Commit ID is null. We need it to get the deleted file contents.");
        }

        var fileText = await Git.GetPreviousCommitContents(commitId, file, serviceDirectory);
        var json = JsonNode.Parse(fileText)?.AsObject() ?? throw new InvalidOperationException("Could not deserialize file contents to JSON.");
        var name = DiagnosticName.From(Diagnostic.Deserialize(json).Name);

        await Diagnostic.Delete(deleteResource, serviceProviderUri, serviceName, name, cancellationToken);
    }

    private async ValueTask PutNamedValueInformationFiles(IReadOnlyCollection<NamedValueInformationFile> files, CancellationToken cancellationToken)
    {
        await Parallel.ForEachAsync(files, cancellationToken, PutNamedValueInformationFile);
    }

    private async ValueTask PutNamedValueInformationFile(NamedValueInformationFile file, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting named value information file {namedValueInformationFile}...", file.Path);

        var json = file.ReadAsJsonObject();
        var namedValue = NamedValue.Deserialize(json);

        var configurationNamedValue = configurationModel?.NamedValues?.FirstOrDefault(configurationNamedValue => configurationNamedValue.Name == namedValue.Name);
        if (configurationNamedValue is not null)
        {
            logger.LogInformation("Found named value {namedValue} in configuration...", namedValue.Name);
            namedValue = namedValue with
            {
                Properties = namedValue.Properties with
                {
                    DisplayName = configurationNamedValue.DisplayName ?? namedValue.Properties.DisplayName,
                    Value = configurationNamedValue.Value ?? namedValue.Properties.Value,
                    KeyVault = configurationNamedValue.KeyVault is null
                                ? namedValue.Properties.KeyVault
                                : namedValue.Properties.KeyVault is null
                                    ? new common.Models.NamedValue.KeyVaultContractCreateProperties
                                    {
                                        IdentityClientId = configurationNamedValue.KeyVault.IdentityClientId,
                                        SecretIdentifier = configurationNamedValue.KeyVault.SecretIdentifier
                                    }
                                    : namedValue.Properties.KeyVault with
                                    {
                                        IdentityClientId = configurationNamedValue.KeyVault.IdentityClientId ?? namedValue.Properties.KeyVault.IdentityClientId,
                                        SecretIdentifier = configurationNamedValue.KeyVault.SecretIdentifier ?? namedValue.Properties.KeyVault.SecretIdentifier
                                    }
                }
            };
        }

        if ((namedValue.Properties.Secret ?? false)
            && (namedValue.Properties.Value is null)
            && (namedValue.Properties.KeyVault?.SecretIdentifier is null))
        {
            logger.LogWarning("Named value {namedValue} is secret, but no value or keyvault identifier was specified. Skipping it...", namedValue.Name);
            return;
        }

        await NamedValue.Put(putResource, serviceProviderUri, serviceName, namedValue, cancellationToken);
    }

    private async ValueTask DeleteNamedValues(IReadOnlyCollection<NamedValueInformationFile> files, CancellationToken cancellationToken)
    {
        await Parallel.ForEachAsync(files, cancellationToken, DeleteNamedValue);
    }

    private async ValueTask DeleteNamedValue(NamedValueInformationFile file, CancellationToken cancellationToken)
    {
        logger.LogInformation("File {namedValueInformationFile} was removed, deleting namedValue...", file.Path);

        if (commitId is null)
        {
            throw new InvalidOperationException("Commit ID is null. We need it to get the deleted file contents.");
        }

        var fileText = await Git.GetPreviousCommitContents(commitId, file, serviceDirectory);
        var json = JsonNode.Parse(fileText)?.AsObject() ?? throw new InvalidOperationException("Could not deserialize file contents to JSON.");
        var name = NamedValueName.From(NamedValue.Deserialize(json).Name);

        await NamedValue.Delete(deleteResource, serviceProviderUri, serviceName, name, cancellationToken);
    }

    private async ValueTask PutProductInformationFiles(IReadOnlyCollection<ProductInformationFile> files, CancellationToken cancellationToken)
    {
        await Parallel.ForEachAsync(files, cancellationToken, PutProductInformationFile);
    }

    private async ValueTask PutProductInformationFile(ProductInformationFile file, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting product information file {productInformationFile}...", file.Path);

        var json = file.ReadAsJsonObject();
        var product = Product.Deserialize(json);

        var configurationProduct = configurationModel?.Products?.FirstOrDefault(configurationProduct => configurationProduct.Name == product.Name);
        if (configurationProduct is not null)
        {
            logger.LogInformation("Found product {product} in configuration...", product.Name);
            product = product with
            {
                Properties = product.Properties with
                {
                    DisplayName = configurationProduct.DisplayName ?? product.Properties.DisplayName,
                    ApprovalRequired = configurationProduct.ApprovalRequired ?? product.Properties.ApprovalRequired,
                    Description = configurationProduct.Description ?? product.Properties.Description,
                    State = configurationProduct.State ?? product.Properties.State,
                    SubscriptionRequired = configurationProduct.SubscriptionRequired ?? product.Properties.SubscriptionRequired,
                    SubscriptionsLimit = configurationProduct.SubscriptionsLimit ?? product.Properties.SubscriptionsLimit,
                    Terms = configurationProduct.Terms ?? product.Properties.Terms
                }
            };
        }

        await Product.Put(putResource, serviceProviderUri, serviceName, product, cancellationToken);
    }

    private async ValueTask DeleteProducts(IReadOnlyCollection<ProductInformationFile> files, CancellationToken cancellationToken)
    {
        await Parallel.ForEachAsync(files, cancellationToken, DeleteProduct);
    }

    private async ValueTask DeleteProduct(ProductInformationFile file, CancellationToken cancellationToken)
    {
        logger.LogInformation("File {productInformationFile} was removed, deleting product...", file.Path);

        if (commitId is null)
        {
            throw new InvalidOperationException("Commit ID is null. We need it to get the deleted file contents.");
        }

        var fileText = await Git.GetPreviousCommitContents(commitId, file, serviceDirectory);
        var json = JsonNode.Parse(fileText)?.AsObject() ?? throw new InvalidOperationException("Could not deserialize file contents to JSON.");
        var name = ProductName.From(Product.Deserialize(json).Name);

        await Product.Delete(deleteResource, serviceProviderUri, serviceName, name, cancellationToken);
    }

    private async ValueTask PutProductApisFiles(IReadOnlyCollection<ProductApisFile> files, CancellationToken cancellationToken)
    {
        await Parallel.ForEachAsync(files, cancellationToken, PutProductApisFile);
    }

    private async ValueTask PutProductApisFile(ProductApisFile file, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting product apis file {productApisFile}...", file.Path);

        var productInformationFile = ProductInformationFile.From(file.ProductDirectory);
        if (productInformationFile.Exists() is false)
        {
            throw new InvalidOperationException($"Product information file is missing. Expected path is {productInformationFile.Path}. Cannot put product APIs file {file.Path}.");
        }

        var productName = Product.GetNameFromFile(productInformationFile);
        var fileApiNames = ProductApi.ListFromFile(file).ToAsyncEnumerable();
        var existingApiNames = ProductApi.List(getResources, serviceProviderUri, serviceName, productName, cancellationToken).Select(api => ApiName.From(api.Name));

        var apiNamesToAdd = fileApiNames.Except(existingApiNames);
        var apiNamesToRemove = existingApiNames.Except(fileApiNames);

        await Parallel.ForEachAsync(apiNamesToAdd, cancellationToken, (apiName, cancellationToken) =>
        {
            logger.LogInformation("Adding product {productName} api {apiName}...", productName, apiName);
            return ProductApi.Put(putResource, serviceProviderUri, serviceName, productName, apiName, cancellationToken);
        });

        await Parallel.ForEachAsync(apiNamesToRemove, cancellationToken, (apiName, cancellationToken) =>
        {
            logger.LogInformation("Removing product {productName} api {apiName}...", productName, apiName);
            return ProductApi.Delete(deleteResource, serviceProviderUri, serviceName, productName, apiName, cancellationToken);
        });
    }

    private async ValueTask DeleteProductApis(IReadOnlyCollection<ProductApisFile> files, CancellationToken cancellationToken)
    {
        await Parallel.ForEachAsync(files, cancellationToken, DeleteProductApis);
    }

    private async ValueTask DeleteProductApis(ProductApisFile file, CancellationToken cancellationToken)
    {
        logger.LogInformation("File {productApisFile} was removed, deleting product APIs...", file.Path);

        var productInformationFile = ProductInformationFile.From(file.ProductDirectory);
        if (productInformationFile.Exists() is false)
        {
            logger.LogWarning("Product information file {productInformationFile} is missing. Cannot get product for {productApisFile}.", productInformationFile.Path, file.Path);
            return;
        }

        var productName = Product.GetNameFromFile(productInformationFile);
        var productApis = ProductApi.List(getResources, serviceProviderUri, serviceName, productName, cancellationToken);
        await Parallel.ForEachAsync(productApis, cancellationToken, (productApi, cancellationToken) =>
        {
            var apiName = ApiName.From(productApi.Name);
            return ProductApi.Delete(deleteResource, serviceProviderUri, serviceName, productName, apiName, cancellationToken);
        });
    }

    private async ValueTask PutProductPolicyFiles(IReadOnlyCollection<ProductPolicyFile> files, CancellationToken cancellationToken)
    {
        await Parallel.ForEachAsync(files, cancellationToken, PutProductPolicyFile);
    }

    private async ValueTask PutProductPolicyFile(ProductPolicyFile file, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting product policy file {productPolicyFile}...", file.Path);

        var productInformationFile = ProductInformationFile.From(file.ProductDirectory);
        if (productInformationFile.Exists() is false)
        {
            throw new InvalidOperationException($"Product information file is missing. Expected path is {productInformationFile.Path}. Cannot put product policy file {file.Path}.");
        }

        var productName = Product.GetNameFromFile(productInformationFile);
        var policyText = await file.ReadAsText(cancellationToken);
        await ProductPolicy.Put(putResource, serviceProviderUri, serviceName, productName, policyText, cancellationToken);
    }

    private async ValueTask DeleteProductPolicies(IReadOnlyCollection<ProductPolicyFile> files, CancellationToken cancellationToken)
    {
        await Parallel.ForEachAsync(files, cancellationToken, DeleteProductPolicy);
    }

    private async ValueTask DeleteProductPolicy(ProductPolicyFile file, CancellationToken cancellationToken)
    {
        logger.LogInformation("File {productPolicyFile} was removed, deleting product policy...", file.Path);

        var productInformationFile = ProductInformationFile.From(file.ProductDirectory);
        if (productInformationFile.Exists() is false)
        {
            logger.LogWarning("Product information file {productInformationFile} is missing. Cannot get product for {productPolicyFile}.", productInformationFile.Path, file.Path);
            return;
        }

        var productName = Product.GetNameFromFile(productInformationFile);
        await ProductPolicy.Delete(deleteResource, serviceProviderUri, serviceName, productName, cancellationToken);
    }

    private async ValueTask PutApiVersionSetInformationFiles(IEnumerable<ApiVersionSetInformationFile> files, CancellationToken cancellationToken)
    {
        await Parallel.ForEachAsync(files, cancellationToken, PutApiVersionSetInformationFile);
    }

    private async ValueTask PutApiVersionSetInformationFile(ApiVersionSetInformationFile informationFile, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting api version set information file {apiInformationFile}...", informationFile.Path);

        var json = informationFile.ReadAsJsonObject();
        var versionSet = ApiVersionSet.Deserialize(json);
        await ApiVersionSet.Put(putResource, serviceProviderUri, serviceName, versionSet, cancellationToken);
    }

    private async ValueTask PutApiInformationAndSpecificationFiles(IReadOnlyCollection<ApiInformationFile> informationFiles, IReadOnlyCollection<ApiSpecificationFile> specificationFiles, CancellationToken cancellationToken)
    {
        var filePairs = informationFiles.FullJoin(specificationFiles,
                                                  informationFile => informationFile.ApiDirectory,
                                                  specificationFile => specificationFile.ApiDirectory,
                                                  informationFile => (InformationFile: informationFile, SpecificationFile: null as ApiSpecificationFile),
                                                  specificationFile => (InformationFile: ApiInformationFile.From(specificationFile.ApiDirectory), SpecificationFile: specificationFile),
                                                  (informationFile, specificationFile) => (InformationFile: informationFile, SpecificationFile: specificationFile));

        // Current revisions need to be processed first or else there's an error.
        var splitCurrentRevisions = filePairs.Select(files =>
        {
            var apiJson = files.InformationFile.ReadAsJsonObject();
            var api = Api.Deserialize(apiJson);
            return (Api: api, SpecificationFile: files.SpecificationFile);
        }).GroupBy(x => x.Api.Properties.IsCurrent == true)
        .ToDictionary(x => x.Key ? "Current" : "NonCurrentRevisions", x => x.ToList());
        if (splitCurrentRevisions.TryGetValue("Current", out var currentRevisions)) await Parallel.ForEachAsync(currentRevisions, cancellationToken, (filePair, cancellationToken) => PutApi(filePair.Api, filePair.SpecificationFile, cancellationToken));
        if (splitCurrentRevisions.TryGetValue("NonCurrentRevisions", out var nonCurrentRevisions)) await Parallel.ForEachAsync(nonCurrentRevisions, cancellationToken, (filePair, cancellationToken) => PutApi(filePair.Api, filePair.SpecificationFile, cancellationToken));
    }

    private async ValueTask PutApi(common.Models.Api api, ApiSpecificationFile? specificationFile, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting api {api}...", api.Name);

        if (specificationFile is not null)
        {
            logger.LogInformation("Updating api with specification file {specificationFile}...", specificationFile.Path);
            // APIM doesn't support Swagger YAML format. We'll convert to Swagger JSON if needed.
            var specification = specificationFile.Specification;

            var format = ApiSpecification.GetApiPropertiesFormat(specification == OpenApiSpecification.V2Yaml
                                                                    ? OpenApiSpecification.V2Json
                                                                    : specification);

            var value = specification == OpenApiSpecification.V2Yaml
                        ? await ApiSpecification.GetFileContentsAsSpecification(specificationFile, OpenApiSpecification.V2Json)
                        : await specificationFile.ReadAsText(cancellationToken);

            api = api with
            {
                Properties = api.Properties with
                {
                    Format = format,
                    Value = value
                }
            };
        }

        var configurationApi = configurationModel?.Apis?.FirstOrDefault(configurationApi => configurationApi.Name == api.Name);
        if (configurationApi is not null)
        {
            logger.LogInformation("Found api {api} in configuration...", api.Name);
            api = api with
            {
                Properties = api.Properties with
                {
                    DisplayName = configurationApi.DisplayName ?? api.Properties.DisplayName,
                    Description = configurationApi.Description ?? api.Properties.Description,
                }
            };
        }

        await Api.Put(putResource, serviceProviderUri, serviceName, api, cancellationToken);
    }

    private async ValueTask DeleteApis(IReadOnlyCollection<ApiInformationFile> files, CancellationToken cancellationToken)
    {
        await Parallel.ForEachAsync(files, cancellationToken, DeleteApi);
    }

    private async ValueTask DeleteApi(ApiInformationFile file, CancellationToken cancellationToken)
    {
        logger.LogInformation("File {apiInformationFile} was removed, deleting api...", file.Path);

        if (commitId is null)
        {
            throw new InvalidOperationException("Commit ID is null. We need it to get the deleted file contents.");
        }

        var fileText = await Git.GetPreviousCommitContents(commitId, file, serviceDirectory);
        var json = JsonNode.Parse(fileText)?.AsObject() ?? throw new InvalidOperationException("Could not deserialize file contents to JSON.");
        var name = ApiName.From(Api.Deserialize(json).Name);

        await Api.Delete(deleteResource, serviceProviderUri, serviceName, name, cancellationToken);
    }

    private async ValueTask PutApiPolicyFiles(IReadOnlyCollection<ApiPolicyFile> files, CancellationToken cancellationToken)
    {
        await Parallel.ForEachAsync(files, cancellationToken, PutApiPolicyFile);
    }

    private async ValueTask PutApiPolicyFile(ApiPolicyFile file, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting api policy file {apiPolicyFile}...", file.Path);

        var apiInformationFile = ApiInformationFile.From(file.ApiDirectory);
        if (apiInformationFile.Exists() is false)
        {
            throw new InvalidOperationException($"Api information file is missing. Expected path is {apiInformationFile.Path}. Cannot put api policy file {file.Path}.");
        }

        var apiName = Api.GetNameFromFile(apiInformationFile);
        var policyText = await file.ReadAsText(cancellationToken);
        await ApiPolicy.Put(putResource, serviceProviderUri, serviceName, apiName, policyText, cancellationToken);
    }

    private async ValueTask DeleteApiPolicies(IReadOnlyCollection<ApiPolicyFile> files, CancellationToken cancellationToken)
    {
        await Parallel.ForEachAsync(files, cancellationToken, DeleteApiPolicy);
    }

    private async ValueTask DeleteApiPolicy(ApiPolicyFile file, CancellationToken cancellationToken)
    {
        logger.LogInformation("File {apiPolicyFile} was removed, deleting api policy...", file.Path);

        var apiInformationFile = ApiInformationFile.From(file.ApiDirectory);
        if (apiInformationFile.Exists() is false)
        {
            logger.LogWarning("Api information file {apiInformationFile} is missing. Cannot get api for {apiPolicyFile}.", apiInformationFile.Path, file.Path);
            return;
        }

        var apiName = Api.GetNameFromFile(apiInformationFile);
        await ApiPolicy.Delete(deleteResource, serviceProviderUri, serviceName, apiName, cancellationToken);
    }

    private async ValueTask PutApiDiagnosticInformationFiles(IReadOnlyCollection<ApiDiagnosticInformationFile> files, CancellationToken cancellationToken)
    {
        await Parallel.ForEachAsync(files, cancellationToken, PutApiDiagnosticInformationFile);
    }

    private async ValueTask PutApiDiagnosticInformationFile(ApiDiagnosticInformationFile file, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting api diagnostic information file {apiDiagnosticInformationFile}...", file.Path);

        var apiInformationFile = ApiInformationFile.From(file.ApiDiagnosticDirectory.ApiDiagnosticsDirectory.ApiDirectory);
        if (apiInformationFile.Exists() is false)
        {
            throw new InvalidOperationException($"Api information file is missing. Expected path is {apiInformationFile.Path}. Cannot put api diagnostic file {file.Path}.");
        }

        var apiName = Api.GetNameFromFile(apiInformationFile);
        var fileJson = file.ReadAsJsonObject();
        var diagnostic = ApiDiagnostic.Deserialize(fileJson);
        var configurationDiagnostic = configurationModel.Apis?.FirstOrDefault(configurationApi => string.Equals(configurationApi.Name, apiName))
                                                             ?.Diagnostics
                                                             ?.FirstOrDefault(configurationDiagnostic => configurationDiagnostic.Name == diagnostic.Name);

        if (configurationDiagnostic is not null)
        {
            logger.LogInformation("Found diagnostic {diagnosticName} for api {apiName} in configuration...", diagnostic.Name, apiName);

            diagnostic = diagnostic with
            {
                Properties = diagnostic.Properties with
                {
                    LoggerId = configurationDiagnostic.LoggerId ?? diagnostic.Properties.LoggerId,
                    Verbosity = configurationDiagnostic.Verbosity ?? diagnostic.Properties.Verbosity
                }
            };
        }

        await ApiDiagnostic.Put(putResource, serviceProviderUri, serviceName, apiName, diagnostic, cancellationToken);
    }

    private async ValueTask DeleteApiDiagnostics(IReadOnlyCollection<ApiDiagnosticInformationFile> files, CancellationToken cancellationToken)
    {
        await Parallel.ForEachAsync(files, cancellationToken, DeleteApiDiagnostic);
    }

    private async ValueTask DeleteApiDiagnostic(ApiDiagnosticInformationFile file, CancellationToken cancellationToken)
    {
        logger.LogInformation("File {apiDiagnosticFile} was removed, deleting api diagnostic...", file.Path);

        var apiInformationFile = ApiInformationFile.From(file.ApiDiagnosticDirectory.ApiDiagnosticsDirectory.ApiDirectory);
        if (apiInformationFile.Exists() is false)
        {
            logger.LogWarning("Api information file {apiInformationFile} is missing. Cannot get api for {apiDiagnosticFile}.", apiInformationFile.Path, file.Path);
            return;
        }

        var apiName = Api.GetNameFromFile(apiInformationFile);
        var diagnosticName = ApiDiagnostic.GetNameFromFile(file);
        await ApiDiagnostic.Delete(deleteResource, serviceProviderUri, serviceName, apiName, diagnosticName, cancellationToken);
    }

    private async ValueTask PutApiOperationPolicyFiles(IReadOnlyCollection<ApiOperationPolicyFile> files, CancellationToken cancellationToken)
    {
        await Parallel.ForEachAsync(files, cancellationToken, PutApiOperationPolicyFile);
    }

    private async ValueTask PutApiOperationPolicyFile(ApiOperationPolicyFile file, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting api operation policy file {apiOperationPolicyFile}...", file.Path);

        var apiDirectory = file.ApiOperationDirectory.ApiOperationsDirectory.ApiDirectory;
        var apiSpecificationFile = ApiSpecification.TryFindFile(apiDirectory)
            ?? throw new InvalidOperationException($"Could not find API specification file for operation policy {file.Path}. Specification file is required to get the operation name.");

        var apiOperationDisplayName = file.ApiOperationDirectory.ApiOperationDisplayName;
        var apiOperationName = await ApiSpecification.TryFindApiOperationName(apiSpecificationFile, apiOperationDisplayName) ?? throw new InvalidOperationException($"Could not find operation with display name {apiOperationDisplayName} in specification file {apiSpecificationFile.Path}.");
        var apiInformationFile = ApiInformationFile.From(apiDirectory);
        var apiName = Api.GetNameFromFile(apiInformationFile);
        var policyText = await file.ReadAsText(cancellationToken);
        await ApiOperationPolicy.Put(putResource, serviceProviderUri, serviceName, apiName, apiOperationName, policyText, cancellationToken);
    }

    private async ValueTask DeleteApiOperationPolicies(IReadOnlyCollection<ApiOperationPolicyFile> files, CancellationToken cancellationToken)
    {
        await Parallel.ForEachAsync(files, cancellationToken, DeleteApiOperationPolicy);
    }

    private async ValueTask DeleteApiOperationPolicy(ApiOperationPolicyFile file, CancellationToken cancellationToken)
    {
        logger.LogInformation("Deleting api operation policy with file {apiOperationPolicyFile}...", file.Path);

        var apiDirectory = file.ApiOperationDirectory.ApiOperationsDirectory.ApiDirectory;
        var apiSpecificationFile = ApiSpecification.TryFindFile(apiDirectory);
        if (apiSpecificationFile is null || apiSpecificationFile.Exists() is false)
        {
            logger.LogWarning("Could not find API specification file for operation policy {operationPolicyFile}. Skipping operation policy deletion...", file.Path);
            return;
        }

        var apiOperationDisplayName = file.ApiOperationDirectory.ApiOperationDisplayName;
        var apiOperationName = await ApiSpecification.TryFindApiOperationName(apiSpecificationFile, apiOperationDisplayName);
        if (apiOperationName is null)
        {
            logger.LogWarning("Could not find API operation {apiOperationDisplayName} in API specification file {apiSpecificationFile}. Skipping operation policy deletion...", apiOperationDisplayName, apiSpecificationFile.Path);
            return;
        }

        var apiInformationFile = ApiInformationFile.From(apiDirectory);
        if (apiInformationFile.Exists() is false)
        {
            logger.LogWarning("Could not find API information file for operation policy {operationPolicyFile}. Skipping operation policy deletion...", file.Path);
            return;
        }

        var apiName = Api.GetNameFromFile(apiInformationFile);

        await ApiOperationPolicy.Delete(deleteResource, serviceProviderUri, serviceName, apiName, apiOperationName, cancellationToken);
    }

    private enum Action
    {
        Put,
        Delete
    }
}