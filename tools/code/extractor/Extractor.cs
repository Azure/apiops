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
    private readonly Func<Uri, CancellationToken, ValueTask<JsonObject?>> tryGetResource;
    private readonly Func<Uri, CancellationToken, ValueTask<JsonObject>> getResource;
    private readonly Func<Uri, CancellationToken, IAsyncEnumerable<JsonObject>> getResources;
    private readonly ServiceDirectory serviceDirectory;
    private readonly ServiceProviderUri serviceProviderUri;
    private readonly ServiceName serviceName;
    private readonly ApiSpecificationFormat specificationFormat;
    private readonly ConfigurationModel configurationModel;

    public Extractor(IHostApplicationLifetime applicationLifetime, ILogger<Extractor> logger, IConfiguration configuration, AzureHttpClient azureHttpClient, NonAuthenticatedHttpClient nonAuthenticatedHttpClient)
    {
        this.applicationLifetime = applicationLifetime;
        this.logger = logger;
        this.nonAuthenticatedHttpClient = nonAuthenticatedHttpClient;
        this.tryGetResource = azureHttpClient.TryGetResourceAsJsonObject;
        this.getResource = azureHttpClient.GetResourceAsJsonObject;
        this.getResources = azureHttpClient.GetResourcesAsJsonObjects;
        this.serviceDirectory = GetServiceDirectory(configuration);
        this.serviceProviderUri = GetServiceProviderUri(configuration, azureHttpClient);
        this.serviceName = ServiceName.From(configuration.GetValue("API_MANAGEMENT_SERVICE_NAME"));
        this.specificationFormat = GetSpecificationFormat(configuration);
        this.configurationModel = configuration.Get<ConfigurationModel>();
    }

    private static ServiceDirectory GetServiceDirectory(IConfiguration configuration) =>
        ServiceDirectory.From(configuration.GetValue("API_MANAGEMENT_SERVICE_OUTPUT_FOLDER_PATH"));

    private static ServiceProviderUri GetServiceProviderUri(IConfiguration configuration, AzureHttpClient azureHttpClient)
    {
        var subscriptionId = configuration.GetValue("AZURE_SUBSCRIPTION_ID");
        var resourceGroupName = configuration.GetValue("AZURE_RESOURCE_GROUP_NAME");

        return ServiceProviderUri.From(azureHttpClient.ResourceManagerEndpoint, subscriptionId, resourceGroupName);
    }

    private static ApiSpecificationFormat GetSpecificationFormat(IConfiguration configuration)
    {
        var configurationFormat = configuration.TryGetValue("API_SPECIFICATION_FORMAT");

        return configurationFormat is null
            ? ApiSpecificationFormat.Yaml
            : Enum.TryParse<ApiSpecificationFormat>(configurationFormat, ignoreCase: true, out var format)
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
        await ExportServiceInformation(cancellationToken);
        await ExportServicePolicy(cancellationToken);
        await ExportNamedValues(cancellationToken);
        await ExportGateways(cancellationToken);
        await ExportLoggers(cancellationToken);
        await ExportProducts(cancellationToken);
        await ExportApis(cancellationToken);
        await ExportVersionSets(cancellationToken);
        await ExportDiagnostics(cancellationToken);
    }

    private async ValueTask ExportServiceInformation(CancellationToken cancellationToken)
    {
        logger.LogInformation("Exporting service information...");

        var file = ServiceInformationFile.From(serviceDirectory);
        var service = await Service.Get(getResource, serviceProviderUri, serviceName, cancellationToken);
        var json = Service.Serialize(service);
        await file.OverwriteWithJson(json, cancellationToken);
    }

    private async ValueTask ExportServicePolicy(CancellationToken cancellationToken)
    {
        var policyText = await ServicePolicy.TryGet(tryGetResource, serviceProviderUri, serviceName, cancellationToken);

        if (policyText is not null)
        {
            logger.LogInformation("Exporting service policy...");

            var file = ServicePolicyFile.From(serviceDirectory);
            await file.OverwriteWithText(policyText, cancellationToken);
        }
    }

    private async ValueTask ExportGateways(CancellationToken cancellationToken)
    {
        var gateways = Gateway.List(getResources, serviceProviderUri, serviceName, cancellationToken);

        await Parallel.ForEachAsync(gateways, cancellationToken, ExportGateway);
    }

    private async ValueTask ExportGateway(common.Models.Gateway gateway, CancellationToken cancellationToken)
    {
        await ExportGatewayInformation(gateway, cancellationToken);
        await ExportGatewayApis(gateway, cancellationToken);
    }

    private async ValueTask ExportGatewayInformation(common.Models.Gateway gateway, CancellationToken cancellationToken)
    {
        logger.LogInformation("Exporting information for gateway {gatewayName}...", gateway.Name);

        var gatewaysDirectory = GatewaysDirectory.From(serviceDirectory);
        var gatewayName = GatewayName.From(gateway.Name);
        var gatewayDirectory = GatewayDirectory.From(gatewaysDirectory, gatewayName);
        var file = GatewayInformationFile.From(gatewayDirectory);
        var json = Gateway.Serialize(gateway);

        await file.OverwriteWithJson(json, cancellationToken);
    }

    private async ValueTask ExportGatewayApis(common.Models.Gateway gateway, CancellationToken cancellationToken)
    {
        var gatewayName = GatewayName.From(gateway.Name);

        var jsonArray = new JsonArray();

        var apis =
            configurationModel.ApiDisplayNames is not null
            ? GatewayApi.List(getResources, serviceProviderUri, serviceName, gatewayName, configurationModel.ApiDisplayNames, cancellationToken)
            : GatewayApi.List(getResources, serviceProviderUri, serviceName, gatewayName, cancellationToken);

        await apis.Select(api => new JsonObject().AddProperty("name", api.Name))
                  .ForEachAsync(jsonObject => jsonArray.Add(jsonObject), cancellationToken);

        if (jsonArray.Any())
        {
            logger.LogInformation("Exporting apis for gateway {gatewayName}...", gateway.Name);
            var gatewaysDirectory = GatewaysDirectory.From(serviceDirectory);
            var gatewayDirectory = GatewayDirectory.From(gatewaysDirectory, gatewayName);
            var file = GatewayApisFile.From(gatewayDirectory);

            await file.OverwriteWithJson(jsonArray, cancellationToken);
        }
    }

    private async ValueTask ExportLoggers(CancellationToken cancellationToken)
    {
        var loggers = Logger.List(getResources, serviceProviderUri, serviceName, cancellationToken);

        await Parallel.ForEachAsync(loggers, cancellationToken, ExportLogger);
    }

    private async ValueTask ExportLogger(common.Models.Logger logger, CancellationToken cancellationToken)
    {
        await ExportLoggerInformation(logger, cancellationToken);
    }

    private async ValueTask ExportLoggerInformation(common.Models.Logger loggerModel, CancellationToken cancellationToken)
    {
        logger.LogInformation("Exporting information for logger {loggerName}...", loggerModel.Name);

        var loggersDirectory = LoggersDirectory.From(serviceDirectory);
        var loggerName = LoggerName.From(loggerModel.Name);
        var loggerDirectory = LoggerDirectory.From(loggersDirectory, loggerName);
        var file = LoggerInformationFile.From(loggerDirectory);
        var json = Logger.Serialize(loggerModel);

        await file.OverwriteWithJson(json, cancellationToken);
    }

    private async ValueTask ExportNamedValues(CancellationToken cancellationToken)
    {
        var namedValues = NamedValue.List(getResources, serviceProviderUri, serviceName, cancellationToken);

        await Parallel.ForEachAsync(namedValues, cancellationToken, ExportNamedValue);
    }

    private async ValueTask ExportNamedValue(common.Models.NamedValue namedValue, CancellationToken cancellationToken)
    {
        await ExportNamedValueInformation(namedValue, cancellationToken);
    }

    private async ValueTask ExportNamedValueInformation(common.Models.NamedValue namedValue, CancellationToken cancellationToken)
    {
        logger.LogInformation("Exporting information for named value {namedValueName}...", namedValue.Name);

        var namedValuesDirectory = NamedValuesDirectory.From(serviceDirectory);
        var namedValueDisplayName = NamedValueDisplayName.From(namedValue.Properties.DisplayName);
        var namedValueDirectory = NamedValueDirectory.From(namedValuesDirectory, namedValueDisplayName);
        var file = NamedValueInformationFile.From(namedValueDirectory);
        var json = NamedValue.Serialize(namedValue);

        await file.OverwriteWithJson(json, cancellationToken);
    }

    private async ValueTask ExportProducts(CancellationToken cancellationToken)
    {
        var products = Product.List(getResources, serviceProviderUri, serviceName, cancellationToken);

        await Parallel.ForEachAsync(products, cancellationToken, ExportProduct);
    }

    private async ValueTask ExportProduct(common.Models.Product product, CancellationToken cancellationToken)
    {
        await ExportProductInformation(product, cancellationToken);
        await ExportProductPolicy(product, cancellationToken);
        await ExportProductApis(product, cancellationToken);
    }

    private async ValueTask ExportProductInformation(common.Models.Product product, CancellationToken cancellationToken)
    {
        logger.LogInformation("Exporting information for product {productName}...", product.Name);

        var productsDirectory = ProductsDirectory.From(serviceDirectory);
        var productDisplayName = ProductDisplayName.From(product.Properties.DisplayName);
        var productDirectory = ProductDirectory.From(productsDirectory, productDisplayName);
        var file = ProductInformationFile.From(productDirectory);
        var json = Product.Serialize(product);

        await file.OverwriteWithJson(json, cancellationToken);
    }

    private async ValueTask ExportProductPolicy(common.Models.Product product, CancellationToken cancellationToken)
    {
        var productName = ProductName.From(product.Name);
        var policyText = await ProductPolicy.TryGet(tryGetResource, serviceProviderUri, serviceName, productName, cancellationToken);

        if (policyText is not null)
        {
            logger.LogInformation("Exporting policy for product {productName}...", product.Name);

            var productsDirectory = ProductsDirectory.From(serviceDirectory);
            var productDisplayName = ProductDisplayName.From(product.Properties.DisplayName);
            var productDirectory = ProductDirectory.From(productsDirectory, productDisplayName);
            var file = ProductPolicyFile.From(productDirectory);

            await file.OverwriteWithText(policyText, cancellationToken);
        }
    }

    private async ValueTask ExportProductApis(common.Models.Product product, CancellationToken cancellationToken)
    {
        var productName = ProductName.From(product.Name);

        var jsonArray = new JsonArray();

        var apis =
            configurationModel.ApiDisplayNames is not null
            ? ProductApi.List(getResources, serviceProviderUri, serviceName, productName, configurationModel.ApiDisplayNames, cancellationToken)
            : ProductApi.List(getResources, serviceProviderUri, serviceName, productName, cancellationToken);

        await apis.Select(api => new JsonObject().AddProperty("name", api.Name))
                  .ForEachAsync(jsonObject => jsonArray.Add(jsonObject), cancellationToken);

        if (jsonArray.Any())
        {
            logger.LogInformation("Exporting apis for product {productName}...", product.Name);
            var productsDirectory = ProductsDirectory.From(serviceDirectory);
            var productDisplayName = ProductDisplayName.From(product.Properties.DisplayName);
            var productDirectory = ProductDirectory.From(productsDirectory, productDisplayName);
            var file = ProductApisFile.From(productDirectory);

            await file.OverwriteWithJson(jsonArray, cancellationToken);
        }
    }

    private async ValueTask ExportDiagnostics(CancellationToken cancellationToken)
    {
        var diagnostics = Diagnostic.List(getResources, serviceProviderUri, serviceName, cancellationToken);

        await Parallel.ForEachAsync(diagnostics, cancellationToken, ExportDiagnostic);
    }

    private async ValueTask ExportDiagnostic(common.Models.Diagnostic diagnostic, CancellationToken cancellationToken)
    {
        await ExportDiagnosticInformation(diagnostic, cancellationToken);
    }

    private async ValueTask ExportDiagnosticInformation(common.Models.Diagnostic diagnostic, CancellationToken cancellationToken)
    {
        logger.LogInformation("Exporting information for diagnostic {diagnosticName}...", diagnostic.Name);

        var diagnosticsDirectory = DiagnosticsDirectory.From(serviceDirectory);
        var diagnosticName = DiagnosticName.From(diagnostic.Name);
        var diagnosticDirectory = DiagnosticDirectory.From(diagnosticsDirectory, diagnosticName);
        var file = DiagnosticInformationFile.From(diagnosticDirectory);
        var json = Diagnostic.Serialize(diagnostic);

        await file.OverwriteWithJson(json, cancellationToken);
    }

    private async ValueTask ExportVersionSets(CancellationToken cancellationToken)
    {
        var versionSets = ApiVersionSet.List(getResources, serviceProviderUri, serviceName, cancellationToken);

        await Parallel.ForEachAsync(versionSets, cancellationToken, ExportVersionSet);
    }

    private async ValueTask ExportVersionSet(common.Models.ApiVersionSet apiVersionSet, CancellationToken cancellationToken)
    {
        logger.LogInformation("Exporting information for version set {versionSetName}...", apiVersionSet.Name);

        var apisDirectory = ApisDirectory.From(serviceDirectory);
        var apiDisplayName = ApiDisplayName.From(apiVersionSet.Properties.DisplayName);

        var apiDirectory = ApiVersionSetDirectory.From(apisDirectory, apiDisplayName);

        var file = ApiVersionSetInformationFile.From(apiDirectory);
        var json = ApiVersionSet.Serialize(apiVersionSet);

        await file.OverwriteWithJson(json, cancellationToken);
    }

    private async ValueTask ExportApis(CancellationToken cancellationToken)
    {
        var apis =
            configurationModel.ApiDisplayNames is not null
            ? Api.List(getResources, serviceProviderUri, serviceName, configurationModel.ApiDisplayNames, cancellationToken)
            : Api.List(getResources, serviceProviderUri, serviceName, cancellationToken);

        await Parallel.ForEachAsync(apis, cancellationToken, ExportApi);
    }

    private async ValueTask ExportApi(common.Models.Api api, CancellationToken cancellationToken)
    {
        await ExportApiInformation(api, cancellationToken);
        await ExportApiPolicy(api, cancellationToken);
        await ExportApiSpecification(api, cancellationToken);
        await ExportApiDiagnostics(api, cancellationToken);
        await ExportApiOperations(api, cancellationToken);
    }

    private async ValueTask ExportApiInformation(common.Models.Api api, CancellationToken cancellationToken)
    {
        logger.LogInformation("Exporting information for api {apiName}...", api.Name);

        var apisDirectory = ApisDirectory.From(serviceDirectory);
        var apiDisplayName = ApiDisplayName.From(api.Properties.DisplayName);
        var apiVersion = ApiVersion.From(api.Properties.ApiVersion);
        var apiRevision = ApiRevision.From(api.Properties.ApiRevision);

        var apiDirectory = ApiDirectory.From(apisDirectory, apiDisplayName, apiVersion, apiRevision);
        var file = ApiInformationFile.From(apiDirectory);
        var json = Api.Serialize(api);

        await file.OverwriteWithJson(json, cancellationToken);
    }

    private async ValueTask ExportApiPolicy(common.Models.Api api, CancellationToken cancellationToken)
    {
        var apiName = ApiName.From(api.Name);
        var policyText = await ApiPolicy.TryGet(tryGetResource, serviceProviderUri, serviceName, apiName, cancellationToken);

        if (policyText is not null)
        {
            logger.LogInformation("Exporting policy for api {apiName}...", api.Name);

            var apisDirectory = ApisDirectory.From(serviceDirectory);
            var apiDisplayName = ApiDisplayName.From(api.Properties.DisplayName);
            var apiVersion = ApiVersion.From(api.Properties.ApiVersion);
            var apiRevision = ApiRevision.From(api.Properties.ApiRevision);
            var apiDirectory = ApiDirectory.From(apisDirectory, apiDisplayName, apiVersion, apiRevision);
            var file = ApiPolicyFile.From(apiDirectory);

            await file.OverwriteWithText(policyText, cancellationToken);
        }
    }

    private async ValueTask ExportApiSpecification(common.Models.Api api, CancellationToken cancellationToken)
    {
        logger.LogInformation("Exporting specification for api {apiName}...", api.Name);

        var apisDirectory = ApisDirectory.From(serviceDirectory);
        var apiDisplayName = ApiDisplayName.From(api.Properties.DisplayName);
        var apiVersion = ApiVersion.From(api.Properties.ApiVersion);
        var apiRevision = ApiRevision.From(api.Properties.ApiRevision);
        var apiDirectory = ApiDirectory.From(apisDirectory, apiDisplayName, apiVersion, apiRevision);
        var file = ApiSpecificationFile.From(apiDirectory, specificationFormat);

        var apiName = ApiName.From(api.Name);
        var downloader = nonAuthenticatedHttpClient.GetSuccessfulResponseStream;
        using var specificationStream = await ApiSpecification.Get(getResource, downloader, serviceProviderUri, serviceName, apiName, specificationFormat, cancellationToken);
        await file.OverwriteWithStream(specificationStream, cancellationToken);
    }

    private async ValueTask ExportApiDiagnostics(common.Models.Api api, CancellationToken cancellationToken)
    {
        var apiName = ApiName.From(api.Name);
        var diagnostics = ApiDiagnostic.List(getResources, serviceProviderUri, serviceName, apiName, cancellationToken);

        await Parallel.ForEachAsync(diagnostics,
                                    cancellationToken,
                                    (diagnostic, cancellationToken) => ExportApiDiagnostic(api, diagnostic, cancellationToken));
    }

    private async ValueTask ExportApiDiagnostic(common.Models.Api api, common.Models.ApiDiagnostic diagnostic, CancellationToken cancellationToken)
    {
        logger.LogInformation("Exporting diagnostic {apiDiagnostic}for api {apiName}...", diagnostic.Name, api.Name);

        var apisDirectory = ApisDirectory.From(serviceDirectory);
        var apiDisplayName = ApiDisplayName.From(api.Properties.DisplayName);
        var apiVersion = ApiVersion.From(api.Properties.ApiVersion);
        var apiRevision = ApiRevision.From(api.Properties.ApiRevision);
        var apiDirectory = ApiDirectory.From(apisDirectory, apiDisplayName, apiVersion, apiRevision);
        var apiDiagnosticsDirectory = ApiDiagnosticsDirectory.From(apiDirectory);
        var apiDiagnosticName = ApiDiagnosticName.From(diagnostic.Name); ;
        var apiDiagnosticDirectory = ApiDiagnosticDirectory.From(apiDiagnosticsDirectory, apiDiagnosticName);
        var file = ApiDiagnosticInformationFile.From(apiDiagnosticDirectory);
        var json = ApiDiagnostic.Serialize(diagnostic);

        await file.OverwriteWithJson(json, cancellationToken);
    }

    private async ValueTask ExportApiOperations(common.Models.Api api, CancellationToken cancellationToken)
    {
        var apiName = ApiName.From(api.Name);
        var apiOperations = ApiOperation.List(getResources, serviceProviderUri, serviceName, apiName, cancellationToken);

        await Parallel.ForEachAsync(apiOperations,
                                    cancellationToken,
                                    (apiOperation, cancellationToken) => ExportApiOperation(api, apiOperation, cancellationToken));
    }

    private async ValueTask ExportApiOperation(common.Models.Api api, common.Models.ApiOperation apiOperation, CancellationToken cancellationToken)
    {
        await ExportApiOperationPolicy(api, apiOperation, cancellationToken);
    }

    private async ValueTask ExportApiOperationPolicy(common.Models.Api api, common.Models.ApiOperation apiOperation, CancellationToken cancellationToken)
    {
        var apiName = ApiName.From(api.Name);
        var apiOperationName = ApiOperationName.From(apiOperation.Name);
        var policyText = await ApiOperationPolicy.TryGet(tryGetResource, serviceProviderUri, serviceName, apiName, apiOperationName, cancellationToken);

        if (policyText is not null)
        {
            logger.LogInformation("Exporting policy for apiOperation {apiOperationName} in api {apiName}...", apiOperation.Name, api.Name);

            var apisDirectory = ApisDirectory.From(serviceDirectory);
            var apiDisplayName = ApiDisplayName.From(api.Properties.DisplayName);
            var apiVersion = ApiVersion.From(api.Properties.ApiVersion);
            var apiRevision = ApiRevision.From(api.Properties.ApiRevision);
            var apiDirectory = ApiDirectory.From(apisDirectory, apiDisplayName, apiVersion, apiRevision);
            var apiOperationsDirectory = ApiOperationsDirectory.From(apiDirectory);
            var apiOperationDisplayName = ApiOperationDisplayName.From(apiOperation.Properties.DisplayName);
            var apiOperationDirectory = ApiOperationDirectory.From(apiOperationsDirectory, apiOperationDisplayName);
            var file = ApiOperationPolicyFile.From(apiOperationDirectory);

            await file.OverwriteWithText(policyText, cancellationToken);
        }
    }
}