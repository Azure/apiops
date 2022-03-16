using common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Readers;
using MoreLinq;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace creator;

internal class Creator : BackgroundService
{
    private readonly IHostApplicationLifetime applicationLifetime;
    private readonly ILogger logger;
    private readonly Func<Uri, CancellationToken, Task<JsonObject>> getResourceAsJsonObject;
    private readonly Func<Uri, CancellationToken, IAsyncEnumerable<JsonObject>> getResourcesAsJsonObjects;
    private readonly ConfigurationModel configurationModel;
    private readonly Func<Uri, JsonObject, CancellationToken, Task> putJsonObject;
    private readonly ServiceDirectory serviceDirectory;
    private readonly ServiceUri serviceUri;
    private readonly CommitId? commitId;

    public Creator(IHostApplicationLifetime applicationLifetime, ILogger<Creator> logger, IConfiguration configuration, AzureHttpClient azureHttpClient)
    {
        this.applicationLifetime = applicationLifetime;
        this.logger = logger;
        this.getResourceAsJsonObject = azureHttpClient.GetResourceAsJsonObject;
        this.getResourcesAsJsonObjects = azureHttpClient.GetResourcesAsJsonObjects;
        this.putJsonObject = azureHttpClient.PutJsonObject;
        this.serviceDirectory = GetServiceDirectory(configuration);
        this.configurationModel = configuration.Get<ConfigurationModel>();
        this.serviceUri = GetServiceUri(configuration, azureHttpClient, serviceDirectory, configurationModel);
        this.commitId = TryGetCommitId(configuration);
    }

    private static ServiceDirectory GetServiceDirectory(IConfiguration configuration) =>
        ServiceDirectory.From(configuration.GetValue("API_MANAGEMENT_SERVICE_OUTPUT_FOLDER_PATH"));

    private static ServiceUri GetServiceUri(IConfiguration configuration, AzureHttpClient azureHttpClient, ServiceDirectory serviceDirectory, ConfigurationModel configurationModel)
    {
        var subscriptionId = configuration.GetValue("AZURE_SUBSCRIPTION_ID");
        var resourceGroupName = configuration.GetValue("AZURE_RESOURCE_GROUP_NAME");
        var serviceProviderUri = azureHttpClient.ResourceManagerEndpoint.AppendPath("subscriptions")
                                                                        .AppendPath(subscriptionId)
                                                                        .AppendPath("resourceGroups")
                                                                        .AppendPath(resourceGroupName)
                                                                        .AppendPath("providers/Microsoft.ApiManagement/service")
                                                                        .SetQueryParameter("api-version", "2021-08-01");
        var getServiceName = () =>
        {
            var configurationServiceName = configuration.TryGetValue("API_MANAGEMENT_SERVICE_NAME");
            var serviceInformationFile = ServiceInformationFile.From(serviceDirectory);
            var jsonServiceName = serviceInformationFile.Exists() ? ServiceName.From(serviceInformationFile) : null;
            var configurationModelServiceName = configurationModel.ApimServiceName;

            return ServiceName.From(configurationModelServiceName ?? jsonServiceName ?? configurationServiceName ?? throw new InvalidOperationException($"Could not find service name."));
        };

        return ServiceUri.From(serviceProviderUri, getServiceName());
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
            logger.LogError(exception, "");
            throw;
        }
        finally
        {
            applicationLifetime.StopApplication();
        }
    }

    private async Task Run(CancellationToken cancellationToken)
    {
        foreach (var fileAction in await GetFilesToProcess())
        {
            var task = fileAction.Key switch
            {
                Action.Delete => DeleteFiles(fileAction.Value),
                Action.Put => PutFiles(fileAction.Value, cancellationToken),
                _ => Task.CompletedTask
            };

            await task;
        }
    }

    private async Task<ImmutableDictionary<Action, ImmutableList<FileRecord>>> GetFilesToProcess() =>
        commitId is null
        ? GetDictionaryFromRootDirectoryFiles()
        : await GetFilesFromCommitId(commitId);

    private async Task<ImmutableDictionary<Action, ImmutableList<FileRecord>>> GetFilesFromCommitId(CommitId commitId)
    {
        var dictionary = await Git.GetFilesFromCommit(commitId, serviceDirectory);

        return dictionary.ToImmutableDictionary(kvp => kvp.Key == CommitStatus.Delete ? Action.Delete : Action.Put, kvp => kvp.Value.Choose(TryClassifyFile).ToImmutableList());
    }

    private ImmutableDictionary<Action, ImmutableList<FileRecord>> GetDictionaryFromRootDirectoryFiles()
    {
        var files = ((DirectoryInfo)serviceDirectory).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true })
                                                     .Choose(TryClassifyFile)
                                                     .ToImmutableList();

        var keyValuePairList = ImmutableList.Create(KeyValuePair.Create(Action.Put, files));

        return ImmutableDictionary.CreateRange(keyValuePairList);
    }

    private FileRecord? TryClassifyFile(FileInfo file) =>
        ServiceInformationFile.TryFrom(serviceDirectory, file) as FileRecord
        ?? ServicePolicyFile.TryFrom(serviceDirectory, file) as FileRecord
        ?? NamedValueInformationFile.TryFrom(serviceDirectory, file) as FileRecord
        ?? GatewayInformationFile.TryFrom(serviceDirectory, file) as FileRecord
        ?? GatewayApisFile.TryFrom(serviceDirectory, file) as FileRecord
        ?? LoggerInformationFile.TryFrom(serviceDirectory, file) as FileRecord
        ?? ProductInformationFile.TryFrom(serviceDirectory, file) as FileRecord
        ?? ProductPolicyFile.TryFrom(serviceDirectory, file) as FileRecord
        ?? ProductApisFile.TryFrom(serviceDirectory, file) as FileRecord
        ?? DiagnosticInformationFile.TryFrom(serviceDirectory, file) as FileRecord
        ?? ApiInformationFile.TryFrom(serviceDirectory, file) as FileRecord
        ?? ApiSpecificationFile.TryFrom(serviceDirectory, file) as FileRecord
        ?? ApiDiagnosticsFile.TryFrom(serviceDirectory, file) as FileRecord
        ?? ApiPolicyFile.TryFrom(serviceDirectory, file) as FileRecord
        ?? ApiOperationPolicyFile.TryFrom(serviceDirectory, file) as FileRecord;

    private async Task DeleteFiles(IReadOnlyCollection<FileRecord> files)
    {
        await Task.CompletedTask;
    }

    private async Task PutFiles(IReadOnlyCollection<FileRecord> files, CancellationToken cancellationToken)
    {
        var putGateways = async () =>
        {
            await PutGatewayInformationFiles(files, cancellationToken);
            await PutGatewayApisFiles(files, cancellationToken);
        };

        var putProducts = async () =>
        {
            await PutProductInformationFiles(files, cancellationToken);
            await Task.WhenAll(PutProductPolicyFiles(files, cancellationToken),
                               PutProductApisFiles(files, cancellationToken));
        };

        var putApis = async () =>
        {
            await PutApiInformationFiles(files, cancellationToken);
            await Task.WhenAll(PutApiPolicyFiles(files, cancellationToken),
                               PutApiDiagnosticsFiles(files, cancellationToken),
                               PutApiOperationPolicyFiles(files, cancellationToken));
        };

        await PutServiceInformationFile(files, cancellationToken);
        await Task.WhenAll(PutServicePolicyFile(files, cancellationToken),
                           PutLoggerInformationFiles(files, cancellationToken),
                           putGateways(),
                           putProducts(),
                           PutDiagnosticInformationFiles(files, cancellationToken),
                           PutNamedValueInformationFiles(files, cancellationToken),
                           putApis());
    }

    private async Task PutServiceInformationFile(IReadOnlyCollection<FileRecord> files, CancellationToken cancellationToken)
    {
        var serviceInformationFile = files.Choose(file => file as ServiceInformationFile).SingleOrDefault();

        if (serviceInformationFile is not null)
        {
            await PutServiceInformationFile(serviceInformationFile, cancellationToken);
        }
    }

    private async Task PutServiceInformationFile(ServiceInformationFile file, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting service information file {serviceInformationFile}...", file.Path);

        var json = file.ReadAsJsonObject();
        var service = Service.FromJsonObject(json);

        var configurationServiceName = configurationModel.ApimServiceName;
        if (configurationServiceName is not null)
        {
            logger.LogInformation("Found service information values in configuration...");
            service = service with { Name = configurationServiceName };
        }

        await PutService(service, cancellationToken);
    }

    private async Task PutService(Service service, CancellationToken cancellationToken)
    {
        var json = service.ToJsonObject();
        await putJsonObject(serviceUri, json, cancellationToken);
    }

    private async Task PutServicePolicyFile(IReadOnlyCollection<FileRecord> files, CancellationToken cancellationToken)
    {
        var servicePolicyFile = files.Choose(file => file as ServicePolicyFile).SingleOrDefault();

        if (servicePolicyFile is not null)
        {
            await PutServicePolicyFile(servicePolicyFile, cancellationToken);
        }
    }

    private async Task PutServicePolicyFile(ServicePolicyFile file, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting service policy file {servicePolicyFile}...", file.Path);

        var policyText = await file.ReadAsText(cancellationToken);
        var json = PolicyTextToJsonObject(policyText);
        var uri = ServicePolicyUri.From(serviceUri);
        await putJsonObject(uri, json, cancellationToken);
    }

    public static JsonObject PolicyTextToJsonObject(string policyText)
    {
        var propertiesJson = new JsonObject().AddProperty("format", "rawxml")
                                             .AddProperty("value", policyText);

        return new JsonObject().AddProperty("properties", propertiesJson);
    }

    private Task PutNamedValueInformationFiles(IReadOnlyCollection<FileRecord> files, CancellationToken cancellationToken) =>
        files.Choose(file => file as NamedValueInformationFile)
             .ExecuteInParallel(PutNamedValueInformationFile, cancellationToken);

    private async Task PutNamedValueInformationFile(NamedValueInformationFile file, CancellationToken cancellationToken)
    {
        logger.LogInformation("Processing named value information file {namedValueInformationFile}...", file.Path);

        var json = file.ReadAsJsonObject();
        var namedValue = NamedValue.FromJsonObject(json);

        var configurationNamedValue = configurationModel.NamedValues?.FirstOrDefault(configurationNamedValue => configurationNamedValue.Name == namedValue.Name);
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

        await PutNamedValue(namedValue, cancellationToken);
    }

    private async Task PutNamedValue(NamedValue namedValue, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting named value {namedValue}...", namedValue.Name);

        var json = namedValue.ToJsonObject();
        var uri = NamedValueUri.From(serviceUri, NamedValueName.From(namedValue.Name));
        await putJsonObject(uri, json, cancellationToken);
    }

    private Task PutGatewayInformationFiles(IReadOnlyCollection<FileRecord> files, CancellationToken cancellationToken) =>
        files.Choose(file => file as GatewayInformationFile)
             .ExecuteInParallel(PutGatewayInformationFile, cancellationToken);

    private async Task PutGatewayInformationFile(GatewayInformationFile file, CancellationToken cancellationToken)
    {
        logger.LogInformation("Processing gateway information file {gatewayInformationFile}...", file.Path);

        var json = file.ReadAsJsonObject();
        var gateway = Gateway.FromJsonObject(json);

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

        await PutGateway(gateway, cancellationToken);
    }

    private async Task PutGateway(Gateway gateway, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting gateway {gateway}...", gateway.Name);

        var json = gateway.ToJsonObject();
        var uri = GatewayUri.From(serviceUri, GatewayName.From(gateway.Name));
        await putJsonObject(uri, json, cancellationToken);
    }

    private Task PutGatewayApisFiles(IReadOnlyCollection<FileRecord> files, CancellationToken cancellationToken) =>
        files.Choose(file => file as GatewayApisFile)
             .ExecuteInParallel(PutGatewayApisFile, cancellationToken);

    private async Task PutGatewayApisFile(GatewayApisFile file, CancellationToken cancellationToken)
    {
        logger.LogInformation("Processing gateway apis file {gatewayApisFile}...", file.Path);

        var gatewayInformationFile = GatewayInformationFile.From(file.GatewayDirectory);
        var gateway = Gateway.FromJsonObject(gatewayInformationFile.ReadAsJsonObject());
        var gatewayApis = file.ReadAsJsonArray().Choose(node => node?.AsObject()).Select(GatewayApi.FromJsonObject);
        var configurationGatewayApis = configurationModel.Gateways?.FirstOrDefault(configurationGateway => configurationGateway.Name == gateway.Name)
                                                                  ?.Apis
                                                                  ?.Where(api => api.Name is not null);
        if (configurationGatewayApis?.Any() ?? false)
        {
            logger.LogInformation("Found apis from gateway {gatewayName} in configuration...", gateway.Name);

            gatewayApis = gatewayApis.FullJoin(configurationGatewayApis,
                                               gatewayApi => gatewayApi.Name,
                                               configurationGatewayApi => configurationGatewayApi.Name,
                                               gatewayApi => gatewayApi,
                                               configurationGatewayApi => new GatewayApi(configurationGatewayApi.Name!),
                                               (gatewayApi, configurationGatewayApi) => gatewayApi);
        }

        await gatewayApis.ExecuteInParallel(gatewayApi => PutGatewayApi(gateway, gatewayApi, cancellationToken), cancellationToken);
    }

    private async Task PutGatewayApi(Gateway gateway, GatewayApi gatewayApi, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting gateway {gatewayName} api {apiName}...", gateway.Name, gatewayApi.Name);

        var gatewayName = GatewayName.From(gateway.Name);
        var gatewayUri = GatewayUri.From(serviceUri, gatewayName);
        var apiName = ApiName.From(gatewayApi.Name);
        var gatewayApiUri = GatewayApiUri.From(gatewayUri, apiName);
        var json = gatewayApi.ToJsonObject();

        await putJsonObject(gatewayApiUri, json, cancellationToken);
    }

    private Task PutLoggerInformationFiles(IReadOnlyCollection<FileRecord> files, CancellationToken cancellationToken) =>
        files.Choose(file => file as LoggerInformationFile)
             .ExecuteInParallel(PutLoggerInformationFile, cancellationToken);

    private async Task PutLoggerInformationFile(LoggerInformationFile file, CancellationToken cancellationToken)
    {
        logger.LogInformation("Processing logger information file {loggerInformationFile}...", file.Path);

        var json = file.ReadAsJsonObject();
        var serviceLogger = Logger.FromJsonObject(json);

        var configurationLogger = configurationModel?.Loggers?.FirstOrDefault(configurationLogger => configurationLogger.Name == serviceLogger.Name);
        if (configurationLogger is not null)
        {
            logger.LogInformation("Found logger {logger} in configuration...", serviceLogger.Name);
            serviceLogger = serviceLogger with
            {
                Properties = serviceLogger.Properties with
                {
                    Credentials = configurationLogger.Credentials is null
                                  ? serviceLogger.Properties.Credentials
                                  : serviceLogger.Properties.Credentials is null
                                    ? new Logger.Credentials
                                    {
                                        Name = configurationLogger.Credentials.Name ?? serviceLogger.Properties.Credentials?.Name,
                                        ConnectionString = configurationLogger.Credentials.ConnectionString ?? serviceLogger.Properties.Credentials?.ConnectionString,
                                        InstrumentationKey = configurationLogger.Credentials.InstrumentationKey ?? serviceLogger.Properties.Credentials?.InstrumentationKey
                                    }
                                    : serviceLogger.Properties.Credentials with
                                    {
                                        Name = configurationLogger.Credentials.Name ?? serviceLogger.Properties.Credentials?.Name,
                                        ConnectionString = configurationLogger.Credentials.ConnectionString ?? serviceLogger.Properties.Credentials?.ConnectionString,
                                        InstrumentationKey = configurationLogger.Credentials.InstrumentationKey ?? serviceLogger.Properties.Credentials?.InstrumentationKey
                                    },
                    Description = configurationLogger.Description ?? serviceLogger.Properties.Description,
                    IsBuffered = configurationLogger.IsBuffered ?? serviceLogger.Properties.IsBuffered,
                    LoggerType = configurationLogger.LoggerType ?? serviceLogger.Properties.LoggerType,
                    ResourceId = configurationLogger.ResourceId ?? serviceLogger.Properties.ResourceId
                }
            };
        }

        await PutLogger(serviceLogger, cancellationToken);
    }

    private async Task PutLogger(Logger serviceLogger, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting logger {logger}...", serviceLogger.Name);

        var json = serviceLogger.ToJsonObject();
        var uri = LoggerUri.From(serviceUri, LoggerName.From(serviceLogger.Name));
        await putJsonObject(uri, json, cancellationToken);
    }

    private Task PutProductInformationFiles(IReadOnlyCollection<FileRecord> files, CancellationToken cancellationToken) =>
        files.Choose(file => file as ProductInformationFile)
             .ExecuteInParallel(PutProductInformationFile, cancellationToken);

    private async Task PutProductInformationFile(ProductInformationFile file, CancellationToken cancellationToken)
    {
        logger.LogInformation("Processing product information file {productInformationFile}...", file.Path);

        var json = file.ReadAsJsonObject();
        var product = Product.FromJsonObject(json);

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

        await PutProduct(product, cancellationToken);
    }

    private async Task PutProduct(Product product, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting product {product}...", product.Name);

        var json = product.ToJsonObject();
        var uri = ProductUri.From(serviceUri, ProductName.From(product.Name));
        await putJsonObject(uri, json, cancellationToken);
    }

    private Task PutProductPolicyFiles(IReadOnlyCollection<FileRecord> files, CancellationToken cancellationToken) =>
        files.Choose(file => file as ProductPolicyFile)
             .ExecuteInParallel(PutProductPolicyFile, cancellationToken);

    private async Task PutProductPolicyFile(ProductPolicyFile file, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting product policy file {productPolicyFile}...", file.Path);

        var policyText = await file.ReadAsText(cancellationToken);
        var json = PolicyTextToJsonObject(policyText);
        var informationFile = ProductInformationFile.From(file.ProductDirectory);
        var name = ProductName.From(informationFile);
        var productUri = ProductUri.From(serviceUri, name);
        var policyUri = ProductPolicyUri.From(productUri);

        await putJsonObject(policyUri, json, cancellationToken);
    }

    private Task PutProductApisFiles(IReadOnlyCollection<FileRecord> files, CancellationToken cancellationToken) =>
        files.Choose(file => file as ProductApisFile)
             .ExecuteInParallel(PutProductApisFile, cancellationToken);

    private async Task PutProductApisFile(ProductApisFile file, CancellationToken cancellationToken)
    {
        logger.LogInformation("Processing product apis file {productApisFile}...", file.Path);

        var productInformationFile = ProductInformationFile.From(file.ProductDirectory);
        var product = Product.FromJsonObject(productInformationFile.ReadAsJsonObject());
        var productApis = file.ReadAsJsonArray().Choose(node => node?.AsObject()).Select(ProductApi.FromJsonObject);
        var configurationProductApis = configurationModel.Products?.FirstOrDefault(configurationProduct => configurationProduct.Name == product.Name)
                                                                  ?.Apis
                                                                  ?.Where(api => api.Name is not null);
        if (configurationProductApis?.Any() ?? false)
        {
            logger.LogInformation("Found apis from product {productName} in configuration...", product.Name);

            productApis = productApis.FullJoin(configurationProductApis,
                                               productApi => productApi.Name,
                                               configurationProductApi => configurationProductApi.Name,
                                               productApi => productApi,
                                               configurationProductApi => new ProductApi(configurationProductApi.Name!),
                                               (productApi, configurationProductApi) => productApi);
        }

        await productApis.ExecuteInParallel(productApi => PutProductApi(product, productApi, cancellationToken), cancellationToken);
    }

    private async Task PutProductApi(Product product, ProductApi productApi, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting product {productName} api {apiName}...", product.Name, productApi.Name);

        var productName = ProductName.From(product.Name);
        var productUri = ProductUri.From(serviceUri, productName);
        var apiName = ApiName.From(productApi.Name);
        var productApiUri = ProductApiUri.From(productUri, apiName);
        var json = productApi.ToJsonObject();

        await putJsonObject(productApiUri, json, cancellationToken);
    }

    private Task PutDiagnosticInformationFiles(IReadOnlyCollection<FileRecord> files, CancellationToken cancellationToken) =>
        files.Choose(file => file as DiagnosticInformationFile)
             .ExecuteInParallel(PutDiagnosticInformationFile, cancellationToken);

    private async Task PutDiagnosticInformationFile(DiagnosticInformationFile file, CancellationToken cancellationToken)
    {
        logger.LogInformation("Processing diagnostic information file {diagnosticInformationFile}...", file.Path);

        var json = file.ReadAsJsonObject();
        var diagnostic = Diagnostic.FromJsonObject(json);

        var configurationDiagnostic = configurationModel.Diagnostics?.FirstOrDefault(configurationDiagnostic => configurationDiagnostic.Name == diagnostic.Name);
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

        await PutDiagnostic(diagnostic, cancellationToken);
    }

    private async Task PutDiagnostic(Diagnostic diagnostic, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting diagnostic {diagnostic}...", diagnostic.Name);

        var json = diagnostic.ToJsonObject();
        var uri = DiagnosticUri.From(serviceUri, DiagnosticName.From(diagnostic.Name));
        await putJsonObject(uri, json, cancellationToken);
    }

    private Task PutApiInformationFiles(IReadOnlyCollection<FileRecord> files, CancellationToken cancellationToken) =>
        files.Choose(file => file as ApiInformationFile)
             .ExecuteInParallel(PutApiInformationFile, cancellationToken);

    private async Task PutApiInformationFile(ApiInformationFile file, CancellationToken cancellationToken)
    {
        logger.LogInformation("Processing api information file {apiInformationFile}...", file.Path);

        var json = file.ReadAsJsonObject();
        var api = Api.FromJsonObject(json);

        var configurationApi = configurationModel?.Apis?.FirstOrDefault(configurationApi => configurationApi.Name == api.Name);
        if (configurationApi is not null)
        {
            logger.LogInformation("Found api {api} in configuration...", api.Name);
            api = api with
            {
                Properties = api.Properties with
                {
                    DisplayName = configurationApi.DisplayName ?? api.Properties.DisplayName,
                    Description = configurationApi.Description ?? api.Properties.Description
                }
            };
        }

        await PutApi(api, cancellationToken);
    }

    private async Task PutApi(Api api, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting api {api}...", api.Name);

        var json = api.ToJsonObject();
        var uri = ApiUri.From(serviceUri, ApiName.From(api.Name));
        await putJsonObject(uri, json, cancellationToken);
    }

    private Task PutApiPolicyFiles(IReadOnlyCollection<FileRecord> files, CancellationToken cancellationToken) =>
        files.Choose(file => file as ApiPolicyFile)
             .ExecuteInParallel(PutApiPolicyFile, cancellationToken);

    private async Task PutApiPolicyFile(ApiPolicyFile file, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting api policy file {apiPolicyFile}...", file.Path);

        var policyText = await file.ReadAsText(cancellationToken);
        var json = PolicyTextToJsonObject(policyText);
        var informationFile = ApiInformationFile.From(file.ApiDirectory);
        var name = ApiName.From(informationFile);
        var apiUri = ApiUri.From(serviceUri, name);
        var policyUri = ApiPolicyUri.From(apiUri);

        await putJsonObject(policyUri, json, cancellationToken);
    }

    private Task PutApiDiagnosticsFiles(IReadOnlyCollection<FileRecord> files, CancellationToken cancellationToken) =>
        files.Choose(file => file as ApiDiagnosticsFile)
             .ExecuteInParallel(PutApiDiagnosticsFile, cancellationToken);

    private async Task PutApiDiagnosticsFile(ApiDiagnosticsFile file, CancellationToken cancellationToken)
    {
        logger.LogInformation("Processing api diagnostics file {apiDiagnosticsFile}...", file.Path);

        var apiInformationFile = ApiInformationFile.From(file.ApiDirectory);
        var api = Api.FromJsonObject(apiInformationFile.ReadAsJsonObject());
        var apiDiagnostics = file.ReadAsJsonArray().Choose(node => node?.AsObject()).Select(ApiDiagnostic.FromJsonObject);
        var configurationApiDiagnostics = configurationModel.Apis?.FirstOrDefault(configurationApi => configurationApi.Name == api.Name)
                                                                  ?.Diagnostics
                                                                  ?.Where(diagnostic => diagnostic.Name is not null);
        if (configurationApiDiagnostics?.Any() ?? false)
        {
            logger.LogInformation("Found diagnostics for api {apiName} in configuration...", api.Name);

            apiDiagnostics = apiDiagnostics.FullJoin(configurationApiDiagnostics,
                                                     apiDiagnostic => apiDiagnostic.Name,
                                                     configurationApiDiagnostic => configurationApiDiagnostic.Name,
                                                     apiDiagnostic => apiDiagnostic,
                                                     configurationApiDiagnostic => new ApiDiagnostic(configurationApiDiagnostic.Name!,
                                                                                                     new ApiDiagnostic.DiagnosticContractProperties(LoggerId: configurationApiDiagnostic.LoggerId ?? throw new InvalidOperationException($"Logger ID was not specified in configuration for diagnostic {configurationApiDiagnostic.Name} in API {api.Name}."))
                                                                                                     {
                                                                                                         Verbosity = configurationApiDiagnostic.Verbosity
                                                                                                     }),
                                                     (apiDiagnostic, configurationApiDiagnostic) => apiDiagnostic);
        }

        await apiDiagnostics.ExecuteInParallel(apiDiagnostic => PutApiDiagnostic(api, apiDiagnostic, cancellationToken), cancellationToken);
    }

    private async Task PutApiDiagnostic(Api api, ApiDiagnostic apiDiagnostic, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting api {apiName} diagnostic {apiDiagnosticName}...", api.Name, apiDiagnostic.Name);

        var apiName = ApiName.From(api.Name);
        var apiUri = ApiUri.From(serviceUri, apiName);
        var apiDiagnosticName = ApiDiagnosticName.From(apiDiagnostic.Name);
        var apiDiagnosticUri = ApiDiagnosticUri.From(apiUri, apiDiagnosticName);
        var json = apiDiagnostic.ToJsonObject();

        await putJsonObject(apiDiagnosticUri, json, cancellationToken);
    }

    private Task PutApiOperationPolicyFiles(IReadOnlyCollection<FileRecord> files, CancellationToken cancellationToken) =>
        files.Choose(file => file as ApiOperationPolicyFile)
             .ExecuteInParallel(PutApiOperationPolicyFile, cancellationToken);

    private async Task PutApiOperationPolicyFile(ApiOperationPolicyFile file, CancellationToken cancellationToken)
    {
        logger.LogInformation("Processing api operation policy file {apiOperationPolicyFile}...", file.Path);

        var apiDirectory = file.ApiOperationDirectory.ApiOperationsDirectory.ApiDirectory;

        var apiSpecificationFile = ApiSpecificationFile.TryFindFirstIn(apiDirectory)
            ?? throw new InvalidOperationException($"Could not find API specification file for operation policy {file.Path}. Specification file is required to get the operation name.");

        var apiOperationDisplayName = file.ApiOperationDirectory.ApiOperationDisplayName;
        var apiOperationName = GetApiOperationNameFromApiSpecificationFile(apiSpecificationFile, apiOperationDisplayName);
        var apiInformationFile = ApiInformationFile.From(apiDirectory);
        var apiName = ApiName.From(apiInformationFile);
        var apiUri = ApiUri.From(serviceUri, apiName);
        var apiOperationUri = ApiOperationUri.From(apiUri, apiOperationName);
        var apiOperationPolicyUri = ApiOperationPolicyUri.From(apiOperationUri);

        var policyText = await file.ReadAsText(cancellationToken);
        var json = PolicyTextToJsonObject(policyText);

        await putJsonObject(apiOperationPolicyUri, json, cancellationToken);
    }

    private static ApiOperationName GetApiOperationNameFromApiSpecificationFile(FileRecord apiSpecificationFile, ApiOperationDisplayName displayName)
    {
        using var stream = apiSpecificationFile.ReadAsStream();
        var specificationDocument = new OpenApiStreamReader().Read(stream, out var _);

        var operation =
            specificationDocument.Paths.Values.SelectMany(pathItem => pathItem.Operations.Values)
                                              .FirstOrDefault(operation => operation.Summary.Equals((string)displayName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Could not find operation with display name {displayName} in specification file {apiSpecificationFile.Path}.");

        return ApiOperationName.From(operation.OperationId);
    }

    private enum Action
    {
        Put,
        Delete
    }
}