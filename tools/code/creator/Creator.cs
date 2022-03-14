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
        foreach (var fileAction in GetFilesToProcess())
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

    private ImmutableDictionary<Action, ImmutableList<FileRecord>> GetFilesToProcess()
    {
        return GetDictionaryFromRootDirectoryFiles();
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
        ?? ApiDiagnosticsFile.TryFrom(serviceDirectory, file) as FileRecord
        ?? ApiPolicyFile.TryFrom(serviceDirectory, file) as FileRecord;

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
                               PutApiDiagnosticsFiles(files, cancellationToken));
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

        var json = await file.ToJsonObject(cancellationToken);
        var uri = ServicePolicyUri.From(serviceUri);
        await putJsonObject(uri, json, cancellationToken);
    }

    private Task PutNamedValueInformationFiles(IReadOnlyCollection<FileRecord> files, CancellationToken cancellationToken) =>
        files.Choose(file => file as NamedValueInformationFile)
             .ExecuteInParallel(PutNamedValueInformationFile, cancellationToken);

    private async Task PutNamedValueInformationFile(NamedValueInformationFile file, CancellationToken cancellationToken)
    {
        logger.LogInformation("Parsing named value information file {namedValueInformationFile}...", file.Path);

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
        logger.LogInformation("Parsing gateway information file {gatewayInformationFile}...", file.Path);

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
        logger.LogInformation("Parsing gateway apis file {gatewayApisFile}...", file.Path);

        var gatewayInformationFile = file.GetGatewayInformationFile();
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
        logger.LogInformation("Parsing logger information file {loggerInformationFile}...", file.Path);

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
        logger.LogInformation("Parsing product information file {productInformationFile}...", file.Path);

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

        var json = await file.ToJsonObject(cancellationToken);
        var informationFile = file.GetProductInformationFile();
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
        logger.LogInformation("Parsing product apis file {productApisFile}...", file.Path);

        var productInformationFile = file.GetProductInformationFile();
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
        logger.LogInformation("Parsing diagnostic information file {diagnosticInformationFile}...", file.Path);

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
        logger.LogInformation("Parsing api information file {apiInformationFile}...", file.Path);

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

        var json = await file.ToJsonObject(cancellationToken);
        var informationFile = file.GetApiInformationFile();
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
        logger.LogInformation("Parsing api diagnostics file {apiDiagnosticsFile}...", file.Path);

        var apiInformationFile = file.GetApiInformationFile();
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

    private enum Action
    {
        Put,
        Delete
    }
}

//public class Creator : ConsoleService
//{
//    private readonly ServiceUri serviceUri;
//    private readonly DirectoryInfo serviceDirectory;
//    private readonly CommitId? commitId;
//    private readonly Func<Uri, CancellationToken, IAsyncEnumerable<JsonObject>> getResources;
//    private readonly Func<Uri, Stream, CancellationToken, Task<Unit>> putStreamResource;
//    private readonly Func<Uri, JsonObject, CancellationToken, Task<Unit>> putJsonObjectResource;
//    private readonly Func<Uri, CancellationToken, Task<Unit>> deleteResource;

//    private void A()
//    {
//        var deserializer = new YamlDotNet.Serialization.Deserializer();

//        deserializer.Deserialize()
//    }

//    public Creator(IHostApplicationLifetime applicationLifetime, ILogger<Creator> logger, IConfiguration configuration, AzureHttpClient azureHttpClient) : base(applicationLifetime, logger)
//    {
//        this.serviceDirectory = GetServiceDirectory(configuration);
//        this.serviceUri = GetServiceUri(configuration, azureHttpClient, serviceDirectory);
//        this.commitId = TryGetCommitId(configuration);
//        this.getResources = azureHttpClient.GetResources;
//        this.putStreamResource = azureHttpClient.PutResource;
//        this.putJsonObjectResource = azureHttpClient.PutResource;
//        this.deleteResource = azureHttpClient.DeleteResource;
//    }

//    private static DirectoryInfo GetServiceDirectory(IConfiguration configuration)
//    {
//        var path = configuration["API_MANAGEMENT_SERVICE_OUTPUT_FOLDER_PATH"];

//        return new DirectoryInfo(path);
//    }

//    private static ServiceUri GetServiceUri(IConfiguration configuration, AzureHttpClient azureHttpClient, DirectoryInfo serviceDirectory)
//    {
//        var subscriptionId = configuration["AZURE_SUBSCRIPTION_ID"];
//        var resourceGroupName = configuration["AZURE_RESOURCE_GROUP_NAME"];
//        var serviceInformationFile = serviceDirectory.GetFileInfo(Constants.ServiceInformationFileName);
//        var serviceName = Service.GetNameFromInformationFile(serviceInformationFile, CancellationToken.None).GetAwaiter().GetResult();

//        var serviceUri = azureHttpClient.BaseUri.AppendPath("subscriptions")
//                                                .AppendPath(subscriptionId)
//                                                .AppendPath("resourceGroups")
//                                                .AppendPath(resourceGroupName)
//                                                .AppendPath("providers/Microsoft.ApiManagement/service")
//                                                .AppendPath(serviceName)
//                                                .SetQueryParameter("api-version", "2021-04-01-preview");

//        return ServiceUri.From(serviceUri);
//    }

//    private static CommitId? TryGetCommitId(IConfiguration configuration)
//    {
//        var configurationSection = configuration.GetSection("COMMIT_ID");

//        return configurationSection.Exists()
//            ? CommitId.From(configurationSection.Value)
//            : null;
//    }

//    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
//    {
//        await GetFilesToProcess().Map(ClassifyFiles)
//                                 .Bind(fileMap => ProcessFiles(fileMap, cancellationToken));
//    }

//    private Task<ILookup<ResourceAction, FileInfo>> GetFilesToProcess()
//    {
//        var matchCommitStatusToAction = (CommitStatus status) => status switch
//            {
//                CommitStatus.Delete => ResourceAction.Delete,
//                _ => ResourceAction.Put
//            };

//        var getLookupFromCommitId = (CommitId commitId) =>
//            Git.GetFilesFromCommit(commitId, serviceDirectory)
//               .Map(lookup => lookup.MapKeys(matchCommitStatusToAction));

//        var getLookupFromDirectory = () =>
//            serviceDirectory.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true })
//                            .ToLookup(_ => ResourceAction.Put);

//        return commitId.Map(getLookupFromCommitId)
//                       .IfNull(() => Task.FromResult(getLookupFromDirectory()));
//    }

//    private ImmutableDictionary<ResourceAction, ILookup<FileType, FileInfo>> ClassifyFiles(ILookup<ResourceAction, FileInfo> fileLookup)
//    {
//        return fileLookup.ToImmutableDictionary(grouping => grouping.Key,
//                                                grouping => grouping.ToLookup(file => FileType.TryGetFileType(serviceDirectory, file))
//                                                                    .RemoveNullKeys());
//    }

//    private async Task<Unit> ProcessFiles(ImmutableDictionary<ResourceAction, ILookup<FileType, FileInfo>> fileMap, CancellationToken cancellationToken)
//    {
//        foreach (var (resourceAction, fileLookup) in fileMap)
//        {
//            await ProcessFiles(resourceAction, fileLookup, cancellationToken);
//        }

//        return Unit.Default;
//    }

//    private Task<Unit> ProcessFiles(ResourceAction resourceAction, ILookup<FileType, FileInfo> fileLookup, CancellationToken cancellationToken)
//    {
//        return resourceAction switch
//        {
//            ResourceAction.Put => ProcessFilesToPut(fileLookup, cancellationToken),
//            ResourceAction.Delete => ProcessFilesToDelete(fileLookup, cancellationToken),
//            _ => throw new InvalidOperationException($"Resource action {resourceAction} is invalid.")
//        };
//    }

//    private async Task<Unit> ProcessFilesToPut(ILookup<FileType, FileInfo> fileLookup, CancellationToken cancellationToken)
//    {
//        var putServiceInformationFile = () => fileLookup.Lookup(FileType.ServiceInformation)
//                                                        .FirstOrDefault()
//                                                        .Map(file => PutServiceInformation(file, cancellationToken))
//                                                        .IfNull(() => Task.FromResult(Unit.Default));

//        var putAuthorizationServers = () => fileLookup.Lookup(FileType.AuthorizationServerInformation)
//                                                      .ExecuteInParallel(PutAuthorizationServerInformation, cancellationToken);

//        var putGateways = () => fileLookup.Lookup(FileType.GatewayInformation)
//                                          .ExecuteInParallel(PutGatewayInformation, cancellationToken);

//        var putServicePolicy = () => fileLookup.Lookup(FileType.ServicePolicy)
//                                                .FirstOrDefault()
//                                                .Map(file => PutServicePolicy(file, cancellationToken))
//                                                .IfNull(() => Task.FromResult(Unit.Default));

//        var putProducts = () => fileLookup.Lookup(FileType.ProductInformation)
//                                          .ExecuteInParallel(PutProductInformation, cancellationToken);

//        var putProductPolicies = () => fileLookup.Lookup(FileType.ProductPolicy)
//                                                 .ExecuteInParallel(PutProductPolicy, cancellationToken);

//        var putLoggers = () => fileLookup.Lookup(FileType.LoggerInformation)
//                                         .ExecuteInParallel(PutLoggerInformation, cancellationToken);

//        var putServiceDiagnostics = () => fileLookup.Lookup(FileType.ServiceDiagnosticInformation)
//                                                    .ExecuteInParallel(PutServiceDiagnosticInformation, cancellationToken);

//        var putApiInformation = () =>
//        {
//            var getInformationFileFromSpecificationFile = (FileInfo specificationFile) => specificationFile.GetDirectoryInfo()
//                                                                                                           .GetFileInfo(Constants.ApiInformationFileName);

//            var jsonSpecificationFiles = fileLookup.Lookup(FileType.ApiJsonSpecification);
//            var yamlSpecificationFiles = fileLookup.Lookup(FileType.ApiYamlSpecification);
//            var specificationFiles = jsonSpecificationFiles.Concat(yamlSpecificationFiles);
//            var apiInformationFiles = fileLookup.Lookup(FileType.ApiInformation);

//            return specificationFiles.Select(getInformationFileFromSpecificationFile)
//                                     .Concat(apiInformationFiles)
//                                     .DistinctBy(file => file.FullName.Normalize())
//                                     .ExecuteInParallel(PutApiInformation, cancellationToken);
//        };

//        var putApiDiagnostics = () => fileLookup.Lookup(FileType.ApiDiagnosticInformation)
//                                                .ExecuteInParallel(PutApiDiagnosticInformation, cancellationToken);

//        var putApiPolicies = () => fileLookup.Lookup(FileType.ApiPolicy)
//                                             .ExecuteInParallel(PutApiPolicy, cancellationToken);

//        var putOperationPolicies = () => fileLookup.Lookup(FileType.OperationPolicy)
//                                                   .ExecuteInParallel(PutOperationPolicy, cancellationToken);

//        await putServiceInformationFile();

//        await Task.WhenAll(putAuthorizationServers(),
//                           putGateways(),
//                           putServicePolicy(),
//                           putProducts(),
//                           putLoggers());

//        await Task.WhenAll(putProductPolicies(), putServiceDiagnostics());

//        await putApiInformation();

//        await Task.WhenAll(putApiPolicies(), putApiDiagnostics(), putOperationPolicies());

//        return Unit.Default;
//    }

//    private async Task<Unit> PutServiceInformation(FileInfo file, CancellationToken cancellationToken)
//    {
//        Logger.LogInformation($"Updating Azure service information with file {file}...");

//        using var stream = file.OpenRead();

//        return await putStreamResource(serviceUri, stream, cancellationToken);
//    }

//    private async Task<Unit> PutAuthorizationServerInformation(FileInfo file, CancellationToken cancellationToken)
//    {
//        Logger.LogInformation($"Updating Azure service authorization server with file {file}...");

//        using var stream = file.OpenRead();
//        var authorizationServerName = await AuthorizationServer.GetNameFromInformationFile(file, cancellationToken);
//        var authorizationServerUri = AuthorizationServer.GetUri(serviceUri, authorizationServerName);

//        return await putStreamResource(authorizationServerUri, stream, cancellationToken);
//    }

//    private async Task<Unit> PutGatewayInformation(FileInfo file, CancellationToken cancellationToken)
//    {
//        Logger.LogInformation($"Updating Azure service gateway with file {file}...");

//        using var stream = file.OpenRead();
//        var gatewayName = await Gateway.GetNameFromInformationFile(file, cancellationToken);
//        var gatewayUri = Gateway.GetUri(serviceUri, gatewayName);

//        await putStreamResource(gatewayUri, stream, cancellationToken);

//        return await SetGatewayApis(file, cancellationToken);
//    }

//    private async Task<Unit> SetGatewayApis(FileInfo file, CancellationToken cancellationToken)
//    {
//        var gatewayName = await Gateway.GetNameFromInformationFile(file, cancellationToken);
//        var gatewayUri = Gateway.GetUri(serviceUri, gatewayName);
//        var listApisUri = Api.GetListByGatewayUri(gatewayUri);

//        var publishedApiDisplayNames = await getResources(listApisUri, cancellationToken).Select(jsonObject => jsonObject.GetObjectPropertyValue("properties")
//                                                                                                                         .GetNonEmptyStringPropertyValue("displayName"))
//                                                                                         .ToListAsync(cancellationToken);

//        var fileApiDisplayNames = await file.ReadAsJsonObject(cancellationToken)
//                                            .Map(json => json.TryGetObjectArrayPropertyValue("apis")
//                                                             .IfNull(() => Enumerable.Empty<JsonObject>())
//                                                             .Select(jsonObject => jsonObject.GetNonEmptyStringPropertyValue("displayName")));

//        var gatewayApisToDelete = publishedApiDisplayNames.ExceptBy(fileApiDisplayNames, displayName => displayName.Normalize());
//        var gatewayApisToCreate = fileApiDisplayNames.ExceptBy(publishedApiDisplayNames, displayName => displayName.Normalize());

//        var deletionTasks = gatewayApisToDelete.Select(displayName => GetApiNameFromServiceUri(displayName, cancellationToken).Bind(apiName => DeleteGatewayApi(gatewayUri, apiName, cancellationToken)));
//        var creationTasks = gatewayApisToCreate.Select(displayName => GetApiNameFromServiceDirectory(displayName, cancellationToken).Bind(apiName => PutGatewayApi(gatewayUri, apiName, cancellationToken)));

//        await Task.WhenAll(deletionTasks.Concat(creationTasks));

//        return Unit.Default;
//    }

//    private async Task<ApiName> GetApiNameFromServiceUri(string apiDisplayName, CancellationToken cancellationToken)
//    {
//        var apiListUri = Api.GetListByServiceUri(serviceUri);

//        return await getResources(apiListUri, cancellationToken).Where(apiJson => apiDisplayName == GetDisplayNameFromResourceJson(apiJson))
//                                                                .Select(apiJson => apiJson.GetNonEmptyStringPropertyValue("name"))
//                                                                .Select(ApiName.From)
//                                                                .FirstOrDefaultAsync(cancellationToken)
//                            ?? throw new InvalidOperationException($"Could not find API with display name {apiDisplayName}.");
//    }

//    private Task<ApiName> GetApiNameFromServiceDirectory(string apiDisplayName, CancellationToken cancellationToken)
//    {
//        return serviceDirectory.GetSubDirectory(Constants.ApisFolderName)
//                               .GetSubDirectory(DirectoryName.From(apiDisplayName))
//                               .GetFileInfo(Constants.ApiInformationFileName)
//                               .ReadAsJsonObject(cancellationToken)
//                               .Map(jsonObject => jsonObject.GetNonEmptyStringPropertyValue("name"))
//                               .Map(ApiName.From);
//    }

//    private async Task<Unit> DeleteGatewayApi(GatewayUri gatewayUri, ApiName apiName, CancellationToken cancellationToken)
//    {
//        var gatewayApiUri = Api.GetUri(gatewayUri, apiName);

//        return await deleteResource(gatewayApiUri, cancellationToken);
//    }

//    private async Task<Unit> PutGatewayApi(GatewayUri gatewayUri, ApiName apiName, CancellationToken cancellationToken)
//    {
//        var gatewayApiUri = Api.GetUri(gatewayUri, apiName);

//        using var payloadStream = new MemoryStream();

//        var payloadJson = new JsonObject().AddProperty("properties",
//                                                       new JsonObject().AddStringProperty("provisioningState", "created"));

//        await payloadJson.SerializeToStream(payloadStream, cancellationToken);

//        return await putStreamResource(gatewayApiUri, payloadStream, cancellationToken);
//    }

//    private Task<Unit> PutServicePolicy(FileInfo file, CancellationToken cancellationToken)
//    {
//        Logger.LogInformation($"Updating Azure service policy with file {file}...");

//        var policyUri = Policy.GetServicePolicyUri(serviceUri);

//        return PutPolicy(policyUri, file, cancellationToken);
//    }

//    private async Task<Unit> PutPolicy(Uri policyUri, FileInfo file, CancellationToken cancellationToken)
//    {
//        var policyText = await file.ReadAsText(cancellationToken);

//        using var stream = new MemoryStream();

//        await new JsonObject().AddStringProperty("format", "rawxml")
//                              .AddStringProperty("value", policyText)
//                              .AddToJsonObject("properties", new JsonObject())
//                              .SerializeToStream(stream, cancellationToken);

//        return await putStreamResource(policyUri, stream, cancellationToken);
//    }

//    private async Task<Unit> PutServiceDiagnosticInformation(FileInfo file, CancellationToken cancellationToken)
//    {
//        var diagnosticName = await Diagnostic.GetNameFromInformationFile(file, cancellationToken);
//        var diagnosticUri = Diagnostic.GetUri(serviceUri, diagnosticName);
//        using var fileStream = file.OpenRead();

//        return await putStreamResource(diagnosticUri, fileStream, cancellationToken);
//    }

//    private async Task<Unit> PutProductInformation(FileInfo file, CancellationToken cancellationToken)
//    {
//        Logger.LogInformation($"Updating Azure service product with file {file}...");

//        using var stream = file.OpenRead();
//        var productName = await Product.GetNameFromInformationFile(file, cancellationToken);
//        var productUri = Product.GetUri(serviceUri, productName);

//        await putStreamResource(productUri, stream, cancellationToken);

//        return await SetProductApis(file, cancellationToken);
//    }

//    private async Task<Unit> SetProductApis(FileInfo file, CancellationToken cancellationToken)
//    {
//        var productName = await Product.GetNameFromInformationFile(file, cancellationToken);
//        var productUri = Product.GetUri(serviceUri, productName);
//        var listApisUri = Api.GetListByProductUri(productUri);

//        var publishedApiDisplayNames = await getResources(listApisUri, cancellationToken).Select(jsonObject => jsonObject.GetObjectPropertyValue("properties")
//                                                                                                                         .GetNonEmptyStringPropertyValue("displayName"))
//                                                                                         .ToListAsync(cancellationToken);

//        var fileApiDisplayNames = await file.ReadAsJsonObject(cancellationToken)
//                                            .Map(json => json.TryGetObjectArrayPropertyValue("apis")
//                                                             .IfNull(() => Enumerable.Empty<JsonObject>())
//                                                             .Select(jsonObject => jsonObject.GetNonEmptyStringPropertyValue("displayName")));

//        var productApisToDelete = publishedApiDisplayNames.ExceptBy(fileApiDisplayNames, displayName => displayName.Normalize());
//        var productApisToCreate = fileApiDisplayNames.ExceptBy(publishedApiDisplayNames, displayName => displayName.Normalize());

//        var deletionTasks = productApisToDelete.Select(displayName => GetApiNameFromServiceUri(displayName, cancellationToken).Bind(apiName => DeleteProductApi(productUri, apiName, cancellationToken)));
//        var creationTasks = productApisToCreate.Select(displayName => GetApiNameFromServiceDirectory(displayName, cancellationToken).Bind(apiName => PutProductApi(productUri, apiName, cancellationToken)));

//        await Task.WhenAll(deletionTasks.Concat(creationTasks));

//        return Unit.Default;
//    }

//    private async Task<Unit> DeleteProductApi(ProductUri productUri, ApiName apiName, CancellationToken cancellationToken)
//    {
//        var productApiUri = Api.GetUri(productUri, apiName);

//        return await deleteResource(productApiUri, cancellationToken);
//    }

//    private async Task<Unit> PutProductApi(ProductUri productUri, ApiName apiName, CancellationToken cancellationToken)
//    {
//        var productApiUri = Api.GetUri(productUri, apiName);

//        using var payloadStream = new MemoryStream();

//        var payloadJson = new JsonObject().AddProperty("properties",
//                                                       new JsonObject().AddStringProperty("provisioningState", "created"));

//        await payloadJson.SerializeToStream(payloadStream, cancellationToken);

//        return await putStreamResource(productApiUri, payloadStream, cancellationToken);
//    }

//    private async Task<Unit> PutProductPolicy(FileInfo file, CancellationToken cancellationToken)
//    {
//        Logger.LogInformation($"Updating Azure product policy with file {file}...");

//        var productName = await Product.GetNameFromPolicyFile(file, cancellationToken);
//        var productUri = Product.GetUri(serviceUri, productName);
//        var policyUri = Policy.GetProductPolicyUri(productUri);

//        return await PutPolicy(policyUri, file, cancellationToken);
//    }

//    private async Task<Unit> PutLoggerInformation(FileInfo file, CancellationToken cancellationToken)
//    {
//        Logger.LogInformation($"Updating Azure logger information with file {file}...");

//        var loggerName = await common.Logger.GetNameFromInformationFile(file, cancellationToken);
//        var loggerUri = common.Logger.GetUri(serviceUri, loggerName);
//        using var fileStream = file.OpenRead();
//        return await putStreamResource(loggerUri, fileStream, cancellationToken);
//    }

//    private async Task<Unit> PutApiInformation(FileInfo file, CancellationToken cancellationToken)
//    {
//        Logger.LogInformation($"Updating Azure API information with file {file}...");

//        var getApiInformationStream = async () =>
//        {
//            var addSpecificationToJson = async (FileName specificationFileName, string specificationFormat, JsonObject apiJson) =>
//            {
//                var specificationFile = file.GetDirectoryInfo().GetFileInfo(specificationFileName);

//                if (specificationFile.Exists)
//                {
//                    Logger.LogInformation($"Adding contents of API specification file {specificationFile}...");
//                    var specification = await specificationFile.ReadAsText(cancellationToken);

//                    var propertiesJson = apiJson.GetObjectPropertyValue("properties")
//                                                .AddStringProperty("format", specificationFormat)
//                                                .AddStringProperty("value", specification);

//                    return apiJson.AddProperty("properties", propertiesJson);
//                }
//                else
//                {
//                    return apiJson;
//                }
//            };

//            var addYamlSpecificationToJson = (JsonObject apiJson) => addSpecificationToJson(Constants.ApiYamlSpecificationFileName, "openapi", apiJson);
//            var addJsonSpecificationToJson = (JsonObject apiJson) => addSpecificationToJson(Constants.ApiJsonSpecificationFileName, "openapi+json", apiJson);

//            var apiJson = await file.ReadAsJsonObject(cancellationToken);
//            apiJson = await addJsonSpecificationToJson(apiJson);
//            apiJson = await addYamlSpecificationToJson(apiJson);

//            var memoryStream = new MemoryStream();
//            await apiJson.SerializeToStream(memoryStream, cancellationToken);
//            return memoryStream;
//        };

//        var apiInformationStream = await getApiInformationStream();
//        var apiName = await Api.GetNameFromInformationFile(file, cancellationToken);
//        var apiUri = Api.GetUri(serviceUri, apiName);

//        return await putStreamResource(apiUri, apiInformationStream, cancellationToken);
//    }

//    private async Task<Unit> PutApiPolicy(FileInfo file, CancellationToken cancellationToken)
//    {
//        Logger.LogInformation($"Updating Azure API policy with file {file}...");

//        var apiName = await Api.GetNameFromPolicyFile(file, cancellationToken);
//        var apiUri = Api.GetUri(serviceUri, apiName);
//        var policyUri = Policy.GetApiPolicyUri(apiUri);

//        return await PutPolicy(policyUri, file, cancellationToken);
//    }

//    private async Task<Unit> PutApiDiagnosticInformation(FileInfo file, CancellationToken cancellationToken)
//    {
//        var apiName = await Api.GetNameFromDiagnosticInformationFile(file, cancellationToken);
//        var apiUri = Api.GetUri(serviceUri, apiName);
//        var diagnosticName = await Diagnostic.GetNameFromInformationFile(file, cancellationToken);
//        var diagnosticUri = Diagnostic.GetUri(apiUri, diagnosticName);
//        using var fileStream = file.OpenRead();

//        return await putStreamResource(diagnosticUri, fileStream, cancellationToken);
//    }

//    private async Task<Unit> PutOperationPolicy(FileInfo file, CancellationToken cancellationToken)
//    {
//        Logger.LogInformation($"Updating Azure API operation policy with file {file}...");

//        var apiName = await Operation.GetApiNameFromPolicyFile(file, cancellationToken);
//        var apiUri = Api.GetUri(serviceUri, apiName);
//        var operationName = Operation.GetNameFromPolicyFile(file);
//        var operationUri = Operation.GetUri(apiUri, operationName);
//        var policyUri = Policy.GetOperationPolicyUri(operationUri);

//        return await PutPolicy(policyUri, file, cancellationToken);
//    }

//    private async Task<Unit> ProcessFilesToDelete(ILookup<FileType, FileInfo> fileLookup, CancellationToken cancellationToken)
//    {
//        var deleteServiceInformationFile = () => fileLookup.Lookup(FileType.ServiceInformation)
//                                                           .FirstOrDefault()
//                                                           .Map(file => DeleteServiceInformation(file, cancellationToken))
//                                                           .IfNull(() => Task.FromResult(Unit.Default));

//        var deleteAuthorizationServers = () => fileLookup.Lookup(FileType.AuthorizationServerInformation)
//                                                         .ExecuteInParallel(DeleteAuthorizationServerInformation, cancellationToken);

//        var deleteGateways = () => fileLookup.Lookup(FileType.GatewayInformation)
//                                             .ExecuteInParallel(DeleteGatewayInformation, cancellationToken);

//        var deleteServicePolicy = () => fileLookup.Lookup(FileType.ServicePolicy)
//                                                  .FirstOrDefault()
//                                                  .Map(file => DeleteServicePolicy(file, cancellationToken))
//                                                  .IfNull(() => Task.FromResult(Unit.Default));

//        var deleteProducts = () => fileLookup.Lookup(FileType.ProductInformation)
//                                             .ExecuteInParallel(DeleteProductInformation, cancellationToken);

//        var deleteProductPolicies = () => fileLookup.Lookup(FileType.ProductPolicy)
//                                                    .ExecuteInParallel(DeleteProductPolicy, cancellationToken);

//        var deleteLoggers = () => fileLookup.Lookup(FileType.LoggerInformation)
//                                            .ExecuteInParallel(DeleteLoggerInformation, cancellationToken);

//        var deleteServiceDiagnostics = () => fileLookup.Lookup(FileType.ServiceDiagnosticInformation)
//                                                       .ExecuteInParallel(DeleteServiceDiagnosticInformation, cancellationToken);

//        var deleteApiInformation = () => fileLookup.Lookup(FileType.ApiInformation)
//                                                   .ExecuteInParallel(DeleteApiInformation, cancellationToken);

//        var deleteApiDiagnostics = () => fileLookup.Lookup(FileType.ApiDiagnosticInformation)
//                                                   .ExecuteInParallel(DeleteApiDiagnosticInformation, cancellationToken);

//        var deleteApiPolicies = () => fileLookup.Lookup(FileType.ApiPolicy)
//                                                .ExecuteInParallel(DeleteApiPolicy, cancellationToken);

//        var deleteOperationPolicies = () => fileLookup.Lookup(FileType.OperationPolicy)
//                                                      .ExecuteInParallel(DeleteOperationPolicy, cancellationToken);

//        await Task.WhenAll(deleteApiPolicies(), deleteApiDiagnostics(), deleteOperationPolicies());

//        await deleteApiInformation();

//        await Task.WhenAll(deleteProductPolicies(), deleteServiceDiagnostics());

//        await Task.WhenAll(deleteAuthorizationServers(),
//                           deleteGateways(),
//                           deleteServicePolicy(),
//                           deleteProducts(),
//                           deleteLoggers());

//        await deleteServiceInformationFile();

//        return Unit.Default;
//    }

//    private Task<Unit> DeleteServiceInformation(FileInfo file, CancellationToken cancellationToken)
//    {
//        Logger.LogInformation($"File {file} was deleted; deleting service information...");

//        throw new NotImplementedException("Delete service manually. For safety reasons, automatic instance deletion was not implemented.");
//    }

//    private Task<Unit> DeleteAuthorizationServerInformation(FileInfo file, CancellationToken cancellationToken)
//    {
//        Logger.LogInformation($"File {file} was deleted; deleting authorization server...");

//        return Git.GetPreviousCommitContents(commitId.IfNullThrow("Commit ID cannot be null."), file, serviceDirectory)
//                  .Map(fileContents => fileContents.ToJsonObject())
//                  .Map(jsonObject => AuthorizationServer.GetNameFromInformationFile(jsonObject))
//                  .Map(name => AuthorizationServer.GetUri(serviceUri, name))
//                  .Bind(uri => deleteResource(uri, cancellationToken));
//    }

//    private Task<Unit> DeleteGatewayInformation(FileInfo file, CancellationToken cancellationToken)
//    {
//        Logger.LogInformation($"File {file} was deleted; deleting gateway...");

//        return Git.GetPreviousCommitContents(commitId.IfNullThrow("Commit ID cannot be null."), file, serviceDirectory)
//                  .Map(fileContents => fileContents.ToJsonObject())
//                  .Map(jsonObject => Gateway.GetNameFromInformationFile(jsonObject))
//                  .Map(name => Gateway.GetUri(serviceUri, name))
//                  .Bind(uri => deleteResource(uri, cancellationToken));
//    }

//    private Task<Unit> DeleteServicePolicy(FileInfo file, CancellationToken cancellationToken)
//    {
//        Logger.LogInformation($"File {file} was deleted; deleting service policy...");

//        var policyUri = Policy.GetServicePolicyUri(serviceUri);

//        return deleteResource(policyUri, cancellationToken);
//    }

//    private Task<Unit> DeleteServiceDiagnosticInformation(FileInfo file, CancellationToken cancellationToken)
//    {
//        Logger.LogInformation($"File {file} was deleted; deleting service diagnostic...");

//        return Git.GetPreviousCommitContents(commitId.IfNullThrow("Commit ID cannot be null."), file, serviceDirectory)
//                  .Map(fileContents => fileContents.ToJsonObject())
//                  .Map(jsonObject => Diagnostic.GetNameFromInformationFile(jsonObject))
//                  .Map(name => Diagnostic.GetUri(serviceUri, name))
//                  .Bind(uri => deleteResource(uri, cancellationToken));
//    }

//    private Task<Unit> DeleteProductInformation(FileInfo file, CancellationToken cancellationToken)
//    {
//        Logger.LogInformation($"File {file} was deleted; deleting service product...");

//        return Git.GetPreviousCommitContents(commitId.IfNullThrow("Commit ID cannot be null."), file, serviceDirectory)
//                  .Map(fileContents => fileContents.ToJsonObject())
//                  .Map(jsonObject => Product.GetNameFromInformationFile(jsonObject))
//                  .Map(name => Product.GetUri(serviceUri, name))
//                  .Bind(uri => deleteResource(uri, cancellationToken));
//    }

//    private static string GetDisplayNameFromResourceJson(JsonObject json)
//    {
//        return json.GetObjectPropertyValue("properties")
//                   .GetNonEmptyStringPropertyValue("displayName");
//    }

//    private async Task<Unit> DeleteProductPolicy(FileInfo productPolicyFile, CancellationToken cancellationToken)
//    {
//        var productInformationFile = Product.GetInformationFileFromPolicyFile(productPolicyFile);

//        if (productInformationFile.Exists)
//        {
//            Logger.LogInformation($"File {productPolicyFile} was deleted; deleting product policy...");

//            var productName = await Product.GetNameFromPolicyFile(productPolicyFile, cancellationToken);
//            var productUri = Product.GetUri(serviceUri, productName);
//            var policyUri = Policy.GetProductPolicyUri(productUri);

//            return await deleteResource(policyUri, cancellationToken);
//        }
//        else
//        {
//            Logger.LogInformation($"Product policy file {productPolicyFile} was deleted, but information file {productInformationFile} is missing; skipping product policy deletion...");
//            return Unit.Default;
//        }
//    }

//    private Task<Unit> DeleteLoggerInformation(FileInfo file, CancellationToken cancellationToken)
//    {
//        Logger.LogInformation($"File {file} was deleted; deleting service logger...");

//        return Git.GetPreviousCommitContents(commitId.IfNullThrow("Commit ID cannot be null."), file, serviceDirectory)
//                  .Map(fileContents => fileContents.ToJsonObject())
//                  .Map(jsonObject => common.Logger.GetNameFromInformationFile(jsonObject))
//                  .Map(name => common.Logger.GetUri(serviceUri, name))
//                  .Bind(uri => deleteResource(uri, cancellationToken));
//    }

//    private Task<Unit> DeleteApiInformation(FileInfo file, CancellationToken cancellationToken)
//    {
//        Logger.LogInformation($"File {file} was deleted; deleting API...");

//        return Git.GetPreviousCommitContents(commitId.IfNullThrow("Commit ID cannot be null."), file, serviceDirectory)
//                  .Map(fileContents => fileContents.ToJsonObject())
//                  .Map(jsonObject => Api.GetNameFromInformationFile(jsonObject))
//                  .Map(name => Api.GetUri(serviceUri, name))
//                  .Bind(uri => deleteResource(uri, cancellationToken));
//    }

//    private async Task<Unit> DeleteApiPolicy(FileInfo apiPolicyFile, CancellationToken cancellationToken)
//    {
//        var apiInformationFile = Api.GetInformationFileFromPolicyFile(apiPolicyFile);

//        if (apiInformationFile.Exists)
//        {
//            Logger.LogInformation($"File {apiPolicyFile} was deleted; deleting API policy...");

//            var apiName = await Api.GetNameFromPolicyFile(apiPolicyFile, cancellationToken);
//            var apiUri = Api.GetUri(serviceUri, apiName);
//            var policyUri = Policy.GetApiPolicyUri(apiUri);

//            return await deleteResource(policyUri, cancellationToken);
//        }
//        else
//        {
//            Logger.LogInformation($"File {apiPolicyFile} was deleted, but information file {apiInformationFile} is missing; skipping API policy deletion...");
//            return Unit.Default;
//        }
//    }

//    private async Task<Unit> DeleteApiDiagnosticInformation(FileInfo apiDiagnosticFile, CancellationToken cancellationToken)
//    {
//        var apiInformationFile = Api.GetInformationFileFromDiagnosticFile(apiDiagnosticFile);

//        if (apiInformationFile.Exists)
//        {
//            Logger.LogInformation($"File {apiDiagnosticFile} was deleted; deleting API diagnostic...");

//            var apiName = await Api.GetNameFromDiagnosticInformationFile(apiDiagnosticFile, cancellationToken);
//            var apiUri = Api.GetUri(serviceUri, apiName);
//            var diagnosticName = DiagnosticName.From(apiDiagnosticFile.GetDirectoryName());
//            var diagnosticUri = Diagnostic.GetUri(apiUri, diagnosticName);

//            return await deleteResource(diagnosticUri, cancellationToken);
//        }
//        else
//        {
//            Logger.LogInformation($"Api diagnostic file {apiDiagnosticFile} was deleted, but information file {apiInformationFile} is missing; skipping API diagnostic deletion...");
//            return Unit.Default;
//        }
//    }

//    private async Task<Unit> DeleteOperationPolicy(FileInfo operationPolicyFile, CancellationToken cancellationToken)
//    {
//        var apiInformationFile = Operation.GetApiInformationFileFromPolicyFile(operationPolicyFile);
//        if (apiInformationFile.Exists)
//        {
//            Logger.LogInformation($"File {operationPolicyFile} was deleted; deleting operation policy...");

//            var apiName = await Api.GetNameFromInformationFile(apiInformationFile, cancellationToken);
//            var apiUri = Api.GetUri(serviceUri, apiName);
//            var operationName = Operation.GetNameFromPolicyFile(operationPolicyFile);
//            var operationUri = Operation.GetUri(apiUri, operationName);
//            var policyUri = Policy.GetOperationPolicyUri(operationUri);

//            return await deleteResource(policyUri, cancellationToken);
//        }
//        else
//        {
//            Logger.LogInformation($"File {operationPolicyFile} was deleted, but information file {apiInformationFile} is missing; skipping operation policy deletion...");
//            return Unit.Default;
//        }
//    }
//}
