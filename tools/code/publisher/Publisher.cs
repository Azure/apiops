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
        this.serviceName = GetServiceName(configuration, serviceDirectory, configurationModel);
        this.commitId = TryGetCommitId(configuration);
    }

    private static ServiceDirectory GetServiceDirectory(IConfiguration configuration) =>
        ServiceDirectory.From(configuration.GetValue("API_MANAGEMENT_SERVICE_OUTPUT_FOLDER_PATH"));

    private static ServiceProviderUri GetServiceProviderUri(IConfiguration configuration, AzureHttpClient azureHttpClient)
    {
        var subscriptionId = configuration.GetValue("AZURE_SUBSCRIPTION_ID");
        var resourceGroupName = configuration.GetValue("AZURE_RESOURCE_GROUP_NAME");

        return ServiceProviderUri.From(azureHttpClient.ResourceManagerEndpoint, subscriptionId, resourceGroupName);
    }

    private static ServiceName GetServiceName(IConfiguration configuration, ServiceDirectory serviceDirectory, ConfigurationModel configurationModel)
    {
        var configurationServiceName = configuration.TryGetValue("API_MANAGEMENT_SERVICE_NAME");
        var serviceInformationFile = ServiceInformationFile.From(serviceDirectory);
        var jsonServiceName = serviceInformationFile.Exists() ? Service.GetNameFromFile(serviceInformationFile) : null;
        var configurationModelServiceName = configurationModel.ApimServiceName;

        return ServiceName.From(configurationModelServiceName ?? jsonServiceName ?? configurationServiceName ?? throw new InvalidOperationException($"Could not find service name."));
    }

    private static CommitId? TryGetCommitId(IConfiguration configuration)
    {
        var commitId = configuration.TryGetValue("COMMIT_ID");

        return commitId is null ? null : CommitId.From(commitId);
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
        var lookup = await GetFilesToProcess(cancellationToken);
        var dictionary = lookup.ToImmutableDictionary(grouping => grouping.Key, grouping => grouping.ToImmutableList());

        if (dictionary.TryGetValue(Action.Delete, out var filesToDelete))
        {
            logger.LogInformation("Deleting files...");
            await DeleteFiles(filesToDelete, cancellationToken);
        }
        else if (dictionary.TryGetValue(Action.Put, out var filesToPut))
        {
            logger.LogInformation("Putting files...");
            await PutFiles(filesToPut, cancellationToken);
        }
    }

    private async ValueTask<ILookup<Action, FileInfo>> GetFilesToProcess(CancellationToken cancellationToken)
    {
        if (commitId is null)
        {
            logger.LogInformation("Commit ID was not specified, getting all files from {serviceDirectory}...", serviceDirectory.Path);
            return GetAllServiceDirectoryFiles();
        }
        else
        {
            logger.LogInformation("Getting files from commit ID {commitId}...", commitId);
            return await GetFilesFromCommitId(commitId, cancellationToken);
        }
    }

    private ILookup<Action, FileInfo> GetAllServiceDirectoryFiles() =>
        ((DirectoryInfo)serviceDirectory).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true })
                                         .ToLookup(_ => Action.Put);

    private async ValueTask<ILookup<Action, FileInfo>> GetFilesFromCommitId(CommitId commitId, CancellationToken cancellationToken)
    {
        var files =
            from grouping in Git.GetFilesFromCommit(commitId, serviceDirectory)
            let action = grouping.Key == CommitStatus.Delete ? Action.Delete : Action.Put
            from file in grouping.ToAsyncEnumerable()
            select (action, file);

        return await files.ToLookupAsync(pair => pair.action, pair => pair.file, cancellationToken);
    }

    private async ValueTask DeleteFiles(IReadOnlyCollection<FileInfo> files, CancellationToken cancellationToken)
    {
        var servicePolicyFiles = new List<ServicePolicyFile>();
        var gatewayInformationFiles = new List<GatewayInformationFile>();
        var namedValueInformationFiles = new List<NamedValueInformationFile>();
        var loggerInformationFiles = new List<LoggerInformationFile>();
        var gatewayApisFiles = new List<GatewayApisFile>();
        var productInformationFiles = new List<ProductInformationFile>();
        var productPolicyFiles = new List<ProductPolicyFile>();
        var productApisFiles = new List<ProductApisFile>();
        var diagnosticInformationFiles = new List<DiagnosticInformationFile>();
        var apiInformationFiles = new List<ApiInformationFile>();
        var apiDiagnosticInformationFiles = new List<ApiDiagnosticInformationFile>();
        var apiPolicyFiles = new List<ApiPolicyFile>();
        var apiOperationPolicyFiles = new List<ApiOperationPolicyFile>();

        foreach (var file in files)
        {
            switch (TryClassifyFile(file))
            {
                case ServicePolicyFile fileRecord: servicePolicyFiles.Add(fileRecord); break;
                case NamedValueInformationFile fileRecord: namedValueInformationFiles.Add(fileRecord); break;
                case GatewayInformationFile fileRecord: gatewayInformationFiles.Add(fileRecord); break;
                case LoggerInformationFile fileRecord: loggerInformationFiles.Add(fileRecord); break;
                case GatewayApisFile fileRecord: gatewayApisFiles.Add(fileRecord); break;
                case ProductInformationFile fileRecord: productInformationFiles.Add(fileRecord); break;
                case ProductPolicyFile fileRecord: productPolicyFiles.Add(fileRecord); break;
                case ProductApisFile fileRecord: productApisFiles.Add(fileRecord); break;
                case DiagnosticInformationFile fileRecord: diagnosticInformationFiles.Add(fileRecord); break;
                case ApiInformationFile fileRecord: apiInformationFiles.Add(fileRecord); break;
                case ApiDiagnosticInformationFile fileRecord: apiDiagnosticInformationFiles.Add(fileRecord); break;
                case ApiPolicyFile fileRecord: apiPolicyFiles.Add(fileRecord); break;
                case ApiOperationPolicyFile fileRecord: apiOperationPolicyFiles.Add(fileRecord); break;
                default: break;
            }
        }

        await DeleteApiOperationPolicies(apiOperationPolicyFiles, cancellationToken);
        await DeleteApiDiagnostics(apiDiagnosticInformationFiles, cancellationToken);
        await DeleteApiPolicies(apiPolicyFiles, cancellationToken);
        await DeleteApis(apiInformationFiles, cancellationToken);
        await DeleteLoggers(loggerInformationFiles, cancellationToken);
        await DeleteGatewayApis(gatewayApisFiles, cancellationToken);
        await DeleteGateways(gatewayInformationFiles, cancellationToken);
        await DeleteProductApis(productApisFiles, cancellationToken);
        await DeleteProductPolicies(productPolicyFiles, cancellationToken);
        await DeleteProducts(productInformationFiles, cancellationToken);
        await DeleteDiagnostics(diagnosticInformationFiles, cancellationToken);
        await DeleteNamedValues(namedValueInformationFiles, cancellationToken);
        await DeleteServicePolicy(servicePolicyFiles, cancellationToken);

        await ValueTask.CompletedTask;
    }

    private async ValueTask PutFiles(IReadOnlyCollection<FileInfo> files, CancellationToken cancellationToken)
    {
        var serviceInformationFiles = new List<ServiceInformationFile>();
        var servicePolicyFiles = new List<ServicePolicyFile>();
        var gatewayInformationFiles = new List<GatewayInformationFile>();
        var namedValueInformationFiles = new List<NamedValueInformationFile>();
        var loggerInformationFiles = new List<LoggerInformationFile>();
        var productInformationFiles = new List<ProductInformationFile>();
        var productPolicyFiles = new List<ProductPolicyFile>();
        var gatewayApisFiles = new List<GatewayApisFile>();
        var productApisFiles = new List<ProductApisFile>();
        var diagnosticInformationFiles = new List<DiagnosticInformationFile>();
        var apiInformationFiles = new List<ApiInformationFile>();
        var apiSpecificationFiles = new List<ApiSpecificationFile>();
        var apiDiagnosticInformationFiles = new List<ApiDiagnosticInformationFile>();
        var apiPolicyFiles = new List<ApiPolicyFile>();
        var apiOperationPolicyFiles = new List<ApiOperationPolicyFile>();

        foreach (var file in files)
        {
            switch (TryClassifyFile(file))
            {
                case ServiceInformationFile fileRecord: serviceInformationFiles.Add(fileRecord); break;
                case ServicePolicyFile fileRecord: servicePolicyFiles.Add(fileRecord); break;
                case GatewayInformationFile fileRecord: gatewayInformationFiles.Add(fileRecord); break;
                case NamedValueInformationFile fileRecord: namedValueInformationFiles.Add(fileRecord); break;
                case LoggerInformationFile fileRecord: loggerInformationFiles.Add(fileRecord); break;
                case ProductInformationFile fileRecord: productInformationFiles.Add(fileRecord); break;
                case GatewayApisFile fileRecord: gatewayApisFiles.Add(fileRecord); break;
                case ProductPolicyFile fileRecord: productPolicyFiles.Add(fileRecord); break;
                case ProductApisFile fileRecord: productApisFiles.Add(fileRecord); break;
                case DiagnosticInformationFile fileRecord: diagnosticInformationFiles.Add(fileRecord); break;
                case ApiInformationFile fileRecord: apiInformationFiles.Add(fileRecord); break;
                case ApiSpecificationFile fileRecord: apiSpecificationFiles.Add(fileRecord); break;
                case ApiDiagnosticInformationFile fileRecord: apiDiagnosticInformationFiles.Add(fileRecord); break;
                case ApiPolicyFile fileRecord: apiPolicyFiles.Add(fileRecord); break;
                case ApiOperationPolicyFile fileRecord: apiOperationPolicyFiles.Add(fileRecord); break;
                default: break;
            }
        }

        await PutServiceInformationFile(serviceInformationFiles, cancellationToken);
        await PutServicePolicyFile(servicePolicyFiles, cancellationToken);
        await PutLoggerInformationFiles(loggerInformationFiles, cancellationToken);
        await PutNamedValueInformationFiles(namedValueInformationFiles, cancellationToken);
        await PutDiagnosticInformationFiles(diagnosticInformationFiles, cancellationToken);
        await PutGatewayInformationFiles(gatewayInformationFiles, cancellationToken);
        await PutProductInformationFiles(productInformationFiles, cancellationToken);
        await PutProductPolicyFiles(productPolicyFiles, cancellationToken);
        await PutApiInformationAndSpecificationFiles(apiInformationFiles, apiSpecificationFiles, cancellationToken);
        await PutApiPolicyFiles(apiPolicyFiles, cancellationToken);
        await PutApiDiagnosticInformationFiles(apiDiagnosticInformationFiles, cancellationToken);
        await PutApiOperationPolicyFiles(apiOperationPolicyFiles, cancellationToken);
        await PutGatewayApisFiles(gatewayApisFiles, cancellationToken);
        await PutProductApisFiles(productApisFiles, cancellationToken);
    }

    private FileRecord? TryClassifyFile(FileInfo file) =>
        ServiceInformationFile.TryFrom(serviceDirectory, file) as FileRecord
        ?? GatewayInformationFile.TryFrom(serviceDirectory, file) as FileRecord
        ?? LoggerInformationFile.TryFrom(serviceDirectory, file) as FileRecord
        ?? ServicePolicyFile.TryFrom(serviceDirectory, file) as FileRecord
        ?? NamedValueInformationFile.TryFrom(serviceDirectory, file) as FileRecord
        ?? ProductInformationFile.TryFrom(serviceDirectory, file) as FileRecord
        ?? GatewayApisFile.TryFrom(serviceDirectory, file) as FileRecord
        ?? ProductPolicyFile.TryFrom(serviceDirectory, file) as FileRecord
        ?? ProductApisFile.TryFrom(serviceDirectory, file) as FileRecord
        ?? DiagnosticInformationFile.TryFrom(serviceDirectory, file) as FileRecord
        ?? ApiInformationFile.TryFrom(serviceDirectory, file) as FileRecord
        ?? ApiSpecificationFile.TryFrom(serviceDirectory, file) as FileRecord
        ?? ApiDiagnosticInformationFile.TryFrom(serviceDirectory, file) as FileRecord
        ?? ApiPolicyFile.TryFrom(serviceDirectory, file) as FileRecord
        ?? ApiOperationPolicyFile.TryFrom(serviceDirectory, file) as FileRecord;

    private async ValueTask PutServiceInformationFile(IReadOnlyCollection<ServiceInformationFile> files, CancellationToken cancellationToken)
    {
        var serviceInformationFile = files.SingleOrDefault();

        if (serviceInformationFile is not null)
        {
            await PutServiceInformationFile(serviceInformationFile, cancellationToken);
        }
    }

    private async ValueTask PutServiceInformationFile(ServiceInformationFile file, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting service information file {serviceInformationFile}...", file.Path);

        var json = file.ReadAsJsonObject();
        var service = Service.Deserialize(json) with { Name = serviceName };
        await Service.Put(putResource, serviceProviderUri, service, cancellationToken);
    }

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
                    Value = configurationNamedValue.Value ?? namedValue.Properties.Value
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

    private async ValueTask PutApiInformationAndSpecificationFiles(IReadOnlyCollection<ApiInformationFile> informationFiles, IReadOnlyCollection<ApiSpecificationFile> specificationFiles, CancellationToken cancellationToken)
    {
        var groups = informationFiles.LeftJoin(specificationFiles,
                                               informationFile => informationFile.ApiDirectory,
                                               specificationFile => specificationFile.ApiDirectory,
                                               informationFile => (InformationFile: informationFile, SpecificationFile: null as ApiSpecificationFile),
                                               (informationFile, specificationFile) => (InformationFile: informationFile, SpecificationFile: specificationFile));

        await Parallel.ForEachAsync(groups, cancellationToken, (group, cancellationToken) => PutApiInformationFile(group.InformationFile, group.SpecificationFile, cancellationToken));
    }

    private async ValueTask PutApiInformationFile(ApiInformationFile informationFile, ApiSpecificationFile? specificationFile, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting api information file {apiInformationFile}...", informationFile.Path);

        var json = informationFile.ReadAsJsonObject();
        var api = Api.Deserialize(json);
        if (specificationFile is not null)
        {
            logger.LogInformation("Updating api with specification file {specificationFile}...", specificationFile.Path);
            api = api with
            {
                Properties = api.Properties with
                {
                    Format = ApiSpecification.FormatToString(specificationFile.Format),
                    Value = await specificationFile.ReadAsText(cancellationToken)
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