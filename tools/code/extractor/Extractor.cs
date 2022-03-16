using common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal class Extractor : BackgroundService
{
    private readonly IHostApplicationLifetime applicationLifetime;
    private readonly ILogger logger;
    private readonly NonAuthenticatedHttpClient nonAuthenticatedHttpClient;
    private readonly Func<Uri, CancellationToken, Task<JsonObject?>> tryGetResourceAsJsonObject;
    private readonly Func<Uri, CancellationToken, Task<JsonObject>> getResourceAsJsonObject;
    private readonly Func<Uri, CancellationToken, IAsyncEnumerable<JsonObject>> getResourcesAsJsonObjects;
    private readonly ServiceDirectory serviceDirectory;
    private readonly ServiceUri serviceUri;
    private readonly ApiSpecificationFile.Format specificationFormat;

    public Extractor(IHostApplicationLifetime applicationLifetime, ILogger<Extractor> logger, IConfiguration configuration, AzureHttpClient azureHttpClient, NonAuthenticatedHttpClient nonAuthenticatedHttpClient)
    {
        this.applicationLifetime = applicationLifetime;
        this.logger = logger;
        this.nonAuthenticatedHttpClient = nonAuthenticatedHttpClient;
        this.tryGetResourceAsJsonObject = azureHttpClient.TryGetResourceAsJsonObject;
        this.getResourceAsJsonObject = azureHttpClient.GetResourceAsJsonObject;
        this.getResourcesAsJsonObjects = azureHttpClient.GetResourcesAsJsonObjects;
        this.serviceDirectory = GetServiceDirectory(configuration);
        this.serviceUri = GetServiceUri(configuration, azureHttpClient);
        this.specificationFormat = GetSpecificationFormat(configuration);
    }

    private static ServiceDirectory GetServiceDirectory(IConfiguration configuration) =>
        ServiceDirectory.From(configuration.GetValue("API_MANAGEMENT_SERVICE_OUTPUT_FOLDER_PATH"));

    private static ServiceUri GetServiceUri(IConfiguration configuration, AzureHttpClient azureHttpClient)
    {
        var subscriptionId = configuration.GetValue("AZURE_SUBSCRIPTION_ID");
        var resourceGroupName = configuration.GetValue("AZURE_RESOURCE_GROUP_NAME");
        var serviceName = ServiceName.From(configuration.GetValue("API_MANAGEMENT_SERVICE_NAME"));

        var serviceProviderUri = azureHttpClient.ResourceManagerEndpoint.AppendPath("subscriptions")
                                                                        .AppendPath(subscriptionId)
                                                                        .AppendPath("resourceGroups")
                                                                        .AppendPath(resourceGroupName)
                                                                        .AppendPath("providers/Microsoft.ApiManagement/service")
                                                                        .SetQueryParameter("api-version", "2021-08-01");

        return ServiceUri.From(serviceProviderUri, serviceName);
    }

    private static ApiSpecificationFile.Format GetSpecificationFormat(IConfiguration configuration)
    {
        var configurationFormat = configuration.TryGetValue("API_SPECIFICATION_FORMAT");

        return configurationFormat is null
            ? ApiSpecificationFile.Format.Yaml
            : Enum.TryParse<ApiSpecificationFile.Format>(configurationFormat, ignoreCase: true, out var format)
              ? format
              : throw new InvalidOperationException("API specification format in configuration is invalid. Accepted values are 'json' and 'yaml'");
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

    private Task Run(CancellationToken cancellationToken) =>
        Task.WhenAll(ExportServiceInformation(cancellationToken),
                     ExportServicePolicy(cancellationToken),
                     ExportGateways(cancellationToken),
                     ExportDiagnostics(cancellationToken),
                     ExportLoggers(cancellationToken),
                     ExportProducts(cancellationToken),
                     ExportNamedValues(cancellationToken),
                     ExportApis(cancellationToken));

    private async Task ExportServiceInformation(CancellationToken cancellationToken)
    {
        var uriJson = await getResourceAsJsonObject(serviceUri, cancellationToken);
        var file = ServiceInformationFile.From(serviceDirectory);
        var parsedJson = Service.FromJsonObject(uriJson).ToJsonObject();

        await file.OverwriteWithJson(parsedJson, cancellationToken);
    }

    private async Task ExportServicePolicy(CancellationToken cancellationToken)
    {
        var uri = ServicePolicyUri.From(serviceUri);
        var uriJson = await tryGetResourceAsJsonObject(uri, cancellationToken);

        if (uriJson is not null)
        {
            var policy = ServicePolicy.GetFromJson(uriJson);
            var file = ServicePolicyFile.From(serviceDirectory);
            await file.OverwriteWithText(policy, cancellationToken);
        }
    }

    private Task ExportNamedValues(CancellationToken cancellationToken)
    {
        var uri = NamedValue.GetListByServiceUri(serviceUri);

        return getResourcesAsJsonObjects(uri, cancellationToken).Select(NamedValue.FromJsonObject)
                                                                .ExecuteInParallel(ExportNamedValueInformation, cancellationToken);
    }

    private async Task ExportNamedValueInformation(NamedValue namedValue, CancellationToken cancellationToken)
    {
        if (namedValue.Properties.Secret ?? false && namedValue.Properties.KeyVault is null)
        {
            logger.LogWarning("Named value {namedValue} with displayName {displayName} has a secret value and is not using Key Vault. Cannot be exported.", namedValue.Name, namedValue.Properties.DisplayName);
            return;
        }

        var namedValuesDirectory = NamedValuesDirectory.From(serviceDirectory);
        var namedValueDisplayName = NamedValueDisplayName.From(namedValue.Properties.DisplayName);
        var namedValueDirectory = NamedValueDirectory.From(namedValuesDirectory, namedValueDisplayName);
        var file = NamedValueInformationFile.From(namedValueDirectory);
        var parsedJson = namedValue.ToJsonObject();

        await file.OverwriteWithJson(parsedJson, cancellationToken);
    }

    private Task ExportGateways(CancellationToken cancellationToken)
    {
        var uri = Gateway.GetListByServiceUri(serviceUri);

        return getResourcesAsJsonObjects(uri, cancellationToken).Select(Gateway.FromJsonObject)
                                                                .ExecuteInParallel(ExportGateway, cancellationToken);
    }

    private Task ExportGateway(Gateway gateway, CancellationToken cancellationToken) =>
        Task.WhenAll(ExportGatewayInformation(gateway, cancellationToken),
                     ExportGatewayApis(gateway, cancellationToken));

    private async Task ExportGatewayInformation(Gateway gateway, CancellationToken cancellationToken)
    {
        var gatewaysDirectory = GatewaysDirectory.From(serviceDirectory);
        var gatewayName = GatewayName.From(gateway.Name);
        var gatewayDirectory = GatewayDirectory.From(gatewaysDirectory, gatewayName);
        var file = GatewayInformationFile.From(gatewayDirectory);
        var parsedJson = gateway.ToJsonObject();

        await file.OverwriteWithJson(parsedJson, cancellationToken);
    }

    private async Task ExportGatewayApis(Gateway gateway, CancellationToken cancellationToken)
    {
        var gatewayName = GatewayName.From(gateway.Name);
        var gatewayUri = GatewayUri.From(serviceUri, gatewayName);
        var gatewayApisUri = GatewayApi.GetListByGatewayUri(gatewayUri);

        var apiJsonObjects =
            await getResourcesAsJsonObjects(gatewayApisUri, cancellationToken).Select(GatewayApi.FromJsonObject)
                                                                              .Select(api => api.ToJsonObject())
                                                                              .ToArrayAsync(cancellationToken);

        if (apiJsonObjects.Any())
        {
            var gatewaysDirectory = GatewaysDirectory.From(serviceDirectory);
            var gatewayDirectory = GatewayDirectory.From(gatewaysDirectory, gatewayName);
            var file = GatewayApisFile.From(gatewayDirectory);

            var apisJsonArray = new JsonArray(apiJsonObjects);

            await file.OverwriteWithJson(apisJsonArray, cancellationToken);
        }
    }

    private Task ExportProducts(CancellationToken cancellationToken)
    {
        var uri = Product.GetListByServiceUri(serviceUri);

        return getResourcesAsJsonObjects(uri, cancellationToken).Select(Product.FromJsonObject)
                                                                .ExecuteInParallel(ExportProduct, cancellationToken);
    }

    private Task ExportProduct(Product product, CancellationToken cancellationToken) =>
        Task.WhenAll(ExportProductInformation(product, cancellationToken),
                     ExportProductApis(product, cancellationToken),
                     ExportProductPolicy(product, cancellationToken));

    private async Task ExportProductInformation(Product product, CancellationToken cancellationToken)
    {
        var productsDirectory = ProductsDirectory.From(serviceDirectory);
        var productDisplayName = ProductDisplayName.From(product.Properties.DisplayName);
        var productDirectory = ProductDirectory.From(productsDirectory, productDisplayName);
        var file = ProductInformationFile.From(productDirectory);

        var parsedJson = product.ToJsonObject();

        await file.OverwriteWithJson(parsedJson, cancellationToken);
    }

    private async Task ExportProductPolicy(Product product, CancellationToken cancellationToken)
    {
        var productName = ProductName.From(product.Name);
        var productUri = ProductUri.From(serviceUri, productName);
        var productPolicyUri = ProductPolicyUri.From(productUri);
        var uriJson = await tryGetResourceAsJsonObject(productPolicyUri, cancellationToken);

        if (uriJson is not null)
        {
            var productsDirectory = ProductsDirectory.From(serviceDirectory);
            var productDisplayName = ProductDisplayName.From(product.Properties.DisplayName);
            var productDirectory = ProductDirectory.From(productsDirectory, productDisplayName);
            var policyFile = ProductPolicyFile.From(productDirectory);

            var policy = ProductPolicy.GetFromJson(uriJson);

            await policyFile.OverwriteWithText(policy, cancellationToken);
        }
    }

    private async Task ExportProductApis(Product product, CancellationToken cancellationToken)
    {
        var productName = ProductName.From(product.Name);
        var productUri = ProductUri.From(serviceUri, productName);
        var productApisUri = ProductApi.GetListByProductUri(productUri);

        var apiJsonObjects =
            await getResourcesAsJsonObjects(productApisUri, cancellationToken).Select(ProductApi.FromJsonObject)
                                                                              .Select(api => api.ToJsonObject())
                                                                              .ToArrayAsync(cancellationToken);

        if (apiJsonObjects.Any())
        {
            var productsDirectory = ProductsDirectory.From(serviceDirectory);
            var productDisplayName = ProductDisplayName.From(product.Properties.DisplayName);
            var productDirectory = ProductDirectory.From(productsDirectory, productDisplayName);
            var file = ProductApisFile.From(productDirectory);

            var apisJsonArray = new JsonArray(apiJsonObjects);

            await file.OverwriteWithJson(apisJsonArray, cancellationToken);
        }
    }

    private Task ExportLoggers(CancellationToken cancellationToken)
    {
        var uri = Logger.GetListByServiceUri(serviceUri);

        return getResourcesAsJsonObjects(uri, cancellationToken).Select(Logger.FromJsonObject)
                                                                .ExecuteInParallel(ExportLoggerInformation, cancellationToken);
    }

    private async Task ExportLoggerInformation(Logger logger, CancellationToken cancellationToken)
    {
        var loggersDirectory = LoggersDirectory.From(serviceDirectory);
        var loggerName = LoggerName.From(logger.Name);
        var loggerDirectory = LoggerDirectory.From(loggersDirectory, loggerName);
        var file = LoggerInformationFile.From(loggerDirectory);

        var parsedJson = logger.ToJsonObject();

        await file.OverwriteWithJson(parsedJson, cancellationToken);
    }

    private Task ExportDiagnostics(CancellationToken cancellationToken)
    {
        var uri = Diagnostic.GetListByServiceUri(serviceUri);

        return getResourcesAsJsonObjects(uri, cancellationToken).Select(Diagnostic.FromJsonObject)
                                                                .ExecuteInParallel(ExportDiagnosticInformation, cancellationToken);
    }

    private async Task ExportDiagnosticInformation(Diagnostic diagnostic, CancellationToken cancellationToken)
    {
        var diagnosticsDirectory = DiagnosticsDirectory.From(serviceDirectory);
        var diagnosticName = DiagnosticName.From(diagnostic.Name);
        var diagnosticDirectory = DiagnosticDirectory.From(diagnosticsDirectory, diagnosticName);
        var file = DiagnosticInformationFile.From(diagnosticDirectory);

        var parsedJson = diagnostic.ToJsonObject();

        await file.OverwriteWithJson(parsedJson, cancellationToken);
    }

    private Task ExportApis(CancellationToken cancellationToken)
    {
        var uri = Api.GetListByServiceUri(serviceUri);

        return getResourcesAsJsonObjects(uri, cancellationToken).Select(Api.FromJsonObject)
                                                                .ExecuteInParallel(ExportApi, cancellationToken);
    }

    private Task ExportApi(Api api, CancellationToken cancellationToken) =>
        Task.WhenAll(ExportApiInformation(api, cancellationToken),
                     ExportApiSpecification(api, cancellationToken),
                     ExportApiPolicy(api, cancellationToken),
                     ExportApiDiagnostics(api, cancellationToken),
                     ExportApiOperations(api, cancellationToken));

    private async Task ExportApiInformation(Api api, CancellationToken cancellationToken)
    {
        var apisDirectory = ApisDirectory.From(serviceDirectory);
        var apiDisplayName = ApiDisplayName.From(api.Properties.DisplayName);
        var apiDirectory = ApiDirectory.From(apisDirectory, apiDisplayName);
        var file = ApiInformationFile.From(apiDirectory);

        var parsedJson = api.ToJsonObject();

        await file.OverwriteWithJson(parsedJson, cancellationToken);
    }

    private async Task ExportApiSpecification(Api api, CancellationToken cancellationToken)
    {
        var apisDirectory = ApisDirectory.From(serviceDirectory);
        var apiDisplayName = ApiDisplayName.From(api.Properties.DisplayName);
        var apiDirectory = ApiDirectory.From(apisDirectory, apiDisplayName);
        var file = ApiSpecificationFile.From(apiDirectory, specificationFormat);

        var apiName = ApiName.From(api.Name);
        var apiUri = ApiUri.From(serviceUri, apiName);
        var downloadUri = await ApiSpecificationFile.GetDownloadUri(apiUri, uri => getResourceAsJsonObject(uri, cancellationToken), specificationFormat);
        using var specificationStream = await nonAuthenticatedHttpClient.GetSuccessfulResponseStream(downloadUri, cancellationToken);

        await file.OverwriteWithStream(specificationStream, cancellationToken);
    }

    private async Task ExportApiPolicy(Api api, CancellationToken cancellationToken)
    {
        var apiName = ApiName.From(api.Name);
        var apiUri = ApiUri.From(serviceUri, apiName);
        var apiPolicyUri = ApiPolicyUri.From(apiUri);
        var uriJson = await tryGetResourceAsJsonObject(apiPolicyUri, cancellationToken);

        if (uriJson is not null)
        {
            var apisDirectory = ApisDirectory.From(serviceDirectory);
            var apiDisplayName = ApiDisplayName.From(api.Properties.DisplayName);
            var apiDirectory = ApiDirectory.From(apisDirectory, apiDisplayName);
            var file = ApiPolicyFile.From(apiDirectory);

            var policy = ApiPolicy.GetFromJson(uriJson);

            await file.OverwriteWithText(policy, cancellationToken);
        }
    }

    private async Task ExportApiDiagnostics(Api api, CancellationToken cancellationToken)
    {
        var apiName = ApiName.From(api.Name);
        var apiUri = ApiUri.From(serviceUri, apiName);
        var apiDiagnosticsUri = ApiDiagnostic.GetListByApiUri(apiUri);

        var diagnosticJsonObjects =
            await getResourcesAsJsonObjects(apiDiagnosticsUri, cancellationToken).Select(ApiDiagnostic.FromJsonObject)
                                                                                 .Select(api => api.ToJsonObject())
                                                                                 .ToArrayAsync(cancellationToken);
        if (diagnosticJsonObjects.Any())
        {
            var apisDirectory = ApisDirectory.From(serviceDirectory);
            var apiDisplayName = ApiDisplayName.From(api.Properties.DisplayName);
            var apiDirectory = ApiDirectory.From(apisDirectory, apiDisplayName);
            var file = ApiDiagnosticsFile.From(apiDirectory);

            var diagnosticsJsonArray = new JsonArray(diagnosticJsonObjects);

            await file.OverwriteWithJson(diagnosticsJsonArray, cancellationToken);
        }
    }

    private async Task ExportApiOperations(Api api, CancellationToken cancellationToken)
    {
        var apiName = ApiName.From(api.Name);
        var apiUri = ApiUri.From(serviceUri, apiName);
        var apiOperationsUri = ApiOperation.GetListByApiUri(apiUri);

        await getResourcesAsJsonObjects(apiOperationsUri, cancellationToken).Select(ApiOperation.FromJsonObject)
                                                                            .ExecuteInParallel(apiOperation => ExportApiOperation(api, apiOperation, cancellationToken),
                                                                                               cancellationToken);
    }

    private Task ExportApiOperation(Api api, ApiOperation apiOperation, CancellationToken cancellationToken) => ExportApiOperationPolicy(api, apiOperation, cancellationToken);

    private async Task ExportApiOperationPolicy(Api api, ApiOperation apiOperation, CancellationToken cancellationToken)
    {
        var apiName = ApiName.From(api.Name);
        var apiUri = ApiUri.From(serviceUri, apiName);
        var apiOperationName = ApiOperationName.From(apiOperation.Name);
        var apiOperationUri = ApiOperationUri.From(apiUri, apiOperationName);
        var apiOperationPolicyUri = ApiOperationPolicyUri.From(apiOperationUri);
        var uriJson = await tryGetResourceAsJsonObject(apiOperationPolicyUri, cancellationToken);

        if (uriJson is not null)
        {
            var apisDirectory = ApisDirectory.From(serviceDirectory);
            var apiDisplayName = ApiDisplayName.From(api.Properties.DisplayName);
            var apiDirectory = ApiDirectory.From(apisDirectory, apiDisplayName);
            var apiOperationsDirectory = ApiOperationsDirectory.From(apiDirectory);
            var apiOperationDisplayName = ApiOperationDisplayName.From(apiOperation.Properties.DisplayName);
            var apiOperationDirectory = ApiOperationDirectory.From(apiOperationsDirectory, apiOperationDisplayName);
            var file = ApiOperationPolicyFile.From(apiOperationDirectory);

            var policy = ApiOperationPolicy.GetFromJson(uriJson);

            await file.OverwriteWithText(policy, cancellationToken);
        }
    }
}