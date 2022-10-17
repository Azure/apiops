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
    private readonly OpenApiSpecification apiSpecification;
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
        this.serviceName = GetServiceName(configuration);
        this.apiSpecification = GetApiSpecification(configuration);
        this.configurationModel = configuration.Get<ConfigurationModel>();
    }

    private static ServiceDirectory GetServiceDirectory(IConfiguration configuration) =>
        ServiceDirectory.From(configuration.GetValue("API_MANAGEMENT_SERVICE_OUTPUT_FOLDER_PATH"));

    private static ServiceProviderUri GetServiceProviderUri(IConfiguration configuration, AzureHttpClient azureHttpClient)
    {
        string subscriptionId = configuration.GetValue("AZURE_SUBSCRIPTION_ID");
        string resourceGroupName = configuration.GetValue("AZURE_RESOURCE_GROUP_NAME");

        return ServiceProviderUri.From(azureHttpClient.ResourceManagerEndpoint, subscriptionId, resourceGroupName);
    }

    private static ServiceName GetServiceName(IConfiguration configuration)
    {
        string? serviceName = configuration.TryGetValue("API_MANAGEMENT_SERVICE_NAME") ?? configuration.TryGetValue("apimServiceName");

        return ServiceName.From(serviceName ?? throw new InvalidOperationException("Could not find service name in configuration. Either specify it in key 'apimServiceName' or 'API_MANAGEMENT_SERVICE_NAME'."));
    }

    private static OpenApiSpecification GetApiSpecification(IConfiguration configuration)
    {
        string? configurationFormat = configuration.TryGetValue("API_SPECIFICATION_FORMAT");

        return configurationFormat is null
            ? OpenApiSpecification.V3Yaml
            : configurationFormat switch
            {
                _ when configurationFormat.Equals("JSON", StringComparison.OrdinalIgnoreCase) => OpenApiSpecification.V3Json,
                _ when configurationFormat.Equals("YAML", StringComparison.OrdinalIgnoreCase) => OpenApiSpecification.V3Yaml,
                _ when configurationFormat.Equals("OpenApiV2Json", StringComparison.OrdinalIgnoreCase) => OpenApiSpecification.V2Json,
                _ when configurationFormat.Equals("OpenApiV2Yaml", StringComparison.OrdinalIgnoreCase) => OpenApiSpecification.V2Yaml,
                _ when configurationFormat.Equals("OpenApiV3Json", StringComparison.OrdinalIgnoreCase) => OpenApiSpecification.V3Json,
                _ when configurationFormat.Equals("OpenApiV3Yaml", StringComparison.OrdinalIgnoreCase) => OpenApiSpecification.V3Yaml,
                _ => throw new InvalidOperationException($"API specification format '{configurationFormat}' defined in configuration is not supported.")
            };
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
        await ExportServicePolicy(cancellationToken);
        await ExportNamedValues(cancellationToken);
        await ExportGateways(cancellationToken);
        await ExportLoggers(cancellationToken);
        await ExportProducts(cancellationToken);
        await ExportApis(cancellationToken);
        await ExportVersionSets(cancellationToken);
        await ExportDiagnostics(cancellationToken);
    }

    private async ValueTask ExportServicePolicy(CancellationToken cancellationToken)
    {
        string? policyText = await ServicePolicy.TryGet(tryGetResource, serviceProviderUri, serviceName, cancellationToken);

        if (policyText is not null)
        {
            logger.LogInformation("Exporting service policy...");

            ServicePolicyFile file = ServicePolicyFile.From(serviceDirectory);
            await file.OverwriteWithText(policyText, cancellationToken);
        }
    }

    private async ValueTask ExportGateways(CancellationToken cancellationToken)
    {
        IAsyncEnumerable<common.Models.Gateway> gateways = Gateway.List(getResources, serviceProviderUri, serviceName, cancellationToken);

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

        GatewaysDirectory gatewaysDirectory = GatewaysDirectory.From(serviceDirectory);
        GatewayName gatewayName = GatewayName.From(gateway.Name);
        GatewayDirectory gatewayDirectory = GatewayDirectory.From(gatewaysDirectory, gatewayName);
        GatewayInformationFile file = GatewayInformationFile.From(gatewayDirectory);
        JsonObject json = Gateway.Serialize(gateway);

        await file.OverwriteWithJson(json, cancellationToken);
    }

    private async ValueTask ExportGatewayApis(common.Models.Gateway gateway, CancellationToken cancellationToken)
    {
        GatewayName gatewayName = GatewayName.From(gateway.Name);

        JsonArray jsonArray = new();

        IAsyncEnumerable<common.Models.Api> apis =
            configurationModel.ApiDisplayNames is not null
            ? GatewayApi.List(getResources, serviceProviderUri, serviceName, gatewayName, configurationModel.ApiDisplayNames, cancellationToken)
            : GatewayApi.List(getResources, serviceProviderUri, serviceName, gatewayName, cancellationToken);

        await apis.Select(api => new JsonObject().AddProperty("name", api.Name))
                  .ForEachAsync(jsonObject => jsonArray.Add(jsonObject), cancellationToken);

        if (jsonArray.Any())
        {
            logger.LogInformation("Exporting apis for gateway {gatewayName}...", gateway.Name);
            GatewaysDirectory gatewaysDirectory = GatewaysDirectory.From(serviceDirectory);
            GatewayDirectory gatewayDirectory = GatewayDirectory.From(gatewaysDirectory, gatewayName);
            GatewayApisFile file = GatewayApisFile.From(gatewayDirectory);

            await file.OverwriteWithJson(jsonArray, cancellationToken);
        }
    }

    private async ValueTask ExportLoggers(CancellationToken cancellationToken)
    {
        IAsyncEnumerable<common.Models.Logger> loggers = Logger.List(getResources, serviceProviderUri, serviceName, cancellationToken);

        await Parallel.ForEachAsync(loggers, cancellationToken, ExportLogger);
    }

    private async ValueTask ExportLogger(common.Models.Logger logger, CancellationToken cancellationToken)
    {
        await ExportLoggerInformation(logger, cancellationToken);
    }

    private async ValueTask ExportLoggerInformation(common.Models.Logger loggerModel, CancellationToken cancellationToken)
    {
        logger.LogInformation("Exporting information for logger {loggerName}...", loggerModel.Name);

        LoggersDirectory loggersDirectory = LoggersDirectory.From(serviceDirectory);
        LoggerName loggerName = LoggerName.From(loggerModel.Name);
        LoggerDirectory loggerDirectory = LoggerDirectory.From(loggersDirectory, loggerName);
        LoggerInformationFile file = LoggerInformationFile.From(loggerDirectory);
        JsonObject json = Logger.Serialize(loggerModel);

        await file.OverwriteWithJson(json, cancellationToken);
    }

    private async ValueTask ExportNamedValues(CancellationToken cancellationToken)
    {
        IAsyncEnumerable<common.Models.NamedValue> namedValues = NamedValue.List(getResources, serviceProviderUri, serviceName, cancellationToken);

        await Parallel.ForEachAsync(namedValues, cancellationToken, ExportNamedValue);
    }

    private async ValueTask ExportNamedValue(common.Models.NamedValue namedValue, CancellationToken cancellationToken)
    {
        await ExportNamedValueInformation(namedValue, cancellationToken);
    }

    private async ValueTask ExportNamedValueInformation(common.Models.NamedValue namedValue, CancellationToken cancellationToken)
    {
        logger.LogInformation("Exporting information for named value {namedValueName}...", namedValue.Name);

        NamedValuesDirectory namedValuesDirectory = NamedValuesDirectory.From(serviceDirectory);
        NamedValueDisplayName namedValueDisplayName = NamedValueDisplayName.From(namedValue.Properties.DisplayName);
        NamedValueDirectory namedValueDirectory = NamedValueDirectory.From(namedValuesDirectory, namedValueDisplayName);
        NamedValueInformationFile file = NamedValueInformationFile.From(namedValueDirectory);
        JsonObject json = NamedValue.Serialize(namedValue);

        await file.OverwriteWithJson(json, cancellationToken);
    }

    private async ValueTask ExportProducts(CancellationToken cancellationToken)
    {
        IAsyncEnumerable<common.Models.Product> products = Product.List(getResources, serviceProviderUri, serviceName, cancellationToken);

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

        ProductsDirectory productsDirectory = ProductsDirectory.From(serviceDirectory);
        ProductDisplayName productDisplayName = ProductDisplayName.From(product.Properties.DisplayName);
        ProductDirectory productDirectory = ProductDirectory.From(productsDirectory, productDisplayName);
        ProductInformationFile file = ProductInformationFile.From(productDirectory);
        JsonObject json = Product.Serialize(product);

        await file.OverwriteWithJson(json, cancellationToken);
    }

    private async ValueTask ExportProductPolicy(common.Models.Product product, CancellationToken cancellationToken)
    {
        ProductName productName = ProductName.From(product.Name);
        string? policyText = await ProductPolicy.TryGet(tryGetResource, serviceProviderUri, serviceName, productName, cancellationToken);

        if (policyText is not null)
        {
            logger.LogInformation("Exporting policy for product {productName}...", product.Name);

            ProductsDirectory productsDirectory = ProductsDirectory.From(serviceDirectory);
            ProductDisplayName productDisplayName = ProductDisplayName.From(product.Properties.DisplayName);
            ProductDirectory productDirectory = ProductDirectory.From(productsDirectory, productDisplayName);
            ProductPolicyFile file = ProductPolicyFile.From(productDirectory);

            await file.OverwriteWithText(policyText, cancellationToken);
        }
    }

    private async ValueTask ExportProductApis(common.Models.Product product, CancellationToken cancellationToken)
    {
        ProductName productName = ProductName.From(product.Name);

        JsonArray jsonArray = new();

        IAsyncEnumerable<common.Models.Api> apis =
            configurationModel.ApiDisplayNames is not null
            ? ProductApi.List(getResources, serviceProviderUri, serviceName, productName, configurationModel.ApiDisplayNames, cancellationToken)
            : ProductApi.List(getResources, serviceProviderUri, serviceName, productName, cancellationToken);

        await apis.Select(api => new JsonObject().AddProperty("name", api.Name))
                  .ForEachAsync(jsonObject => jsonArray.Add(jsonObject), cancellationToken);

        if (jsonArray.Any())
        {
            logger.LogInformation("Exporting apis for product {productName}...", product.Name);
            ProductsDirectory productsDirectory = ProductsDirectory.From(serviceDirectory);
            ProductDisplayName productDisplayName = ProductDisplayName.From(product.Properties.DisplayName);
            ProductDirectory productDirectory = ProductDirectory.From(productsDirectory, productDisplayName);
            ProductApisFile file = ProductApisFile.From(productDirectory);

            await file.OverwriteWithJson(jsonArray, cancellationToken);
        }
    }

    private async ValueTask ExportDiagnostics(CancellationToken cancellationToken)
    {
        IAsyncEnumerable<common.Models.Diagnostic> diagnostics = Diagnostic.List(getResources, serviceProviderUri, serviceName, cancellationToken);

        await Parallel.ForEachAsync(diagnostics, cancellationToken, ExportDiagnostic);
    }

    private async ValueTask ExportDiagnostic(common.Models.Diagnostic diagnostic, CancellationToken cancellationToken)
    {
        await ExportDiagnosticInformation(diagnostic, cancellationToken);
    }

    private async ValueTask ExportDiagnosticInformation(common.Models.Diagnostic diagnostic, CancellationToken cancellationToken)
    {
        logger.LogInformation("Exporting information for diagnostic {diagnosticName}...", diagnostic.Name);

        DiagnosticsDirectory diagnosticsDirectory = DiagnosticsDirectory.From(serviceDirectory);
        DiagnosticName diagnosticName = DiagnosticName.From(diagnostic.Name);
        DiagnosticDirectory diagnosticDirectory = DiagnosticDirectory.From(diagnosticsDirectory, diagnosticName);
        DiagnosticInformationFile file = DiagnosticInformationFile.From(diagnosticDirectory);
        JsonObject json = Diagnostic.Serialize(diagnostic);

        await file.OverwriteWithJson(json, cancellationToken);
    }

    private async ValueTask ExportVersionSets(CancellationToken cancellationToken)
    {
        IAsyncEnumerable<common.Models.ApiVersionSet> versionSets =
            configurationModel.ApiDisplayNames is not null
            ? ApiVersionSet.List(getResources, serviceProviderUri, serviceName, configurationModel.ApiDisplayNames, cancellationToken)
            : ApiVersionSet.List(getResources, serviceProviderUri, serviceName, cancellationToken);

        await Parallel.ForEachAsync(versionSets, cancellationToken, ExportVersionSet);
    }

    private async ValueTask ExportVersionSet(common.Models.ApiVersionSet apiVersionSet, CancellationToken cancellationToken)
    {
        logger.LogInformation("Exporting information for version set {versionSetName}...", apiVersionSet.Name);

        ApisDirectory apisDirectory = ApisDirectory.From(serviceDirectory);
        ApiDisplayName apiDisplayName = ApiDisplayName.From(apiVersionSet.Properties.DisplayName);

        ApiVersionSetDirectory apiDirectory = ApiVersionSetDirectory.From(apisDirectory, apiDisplayName);

        ApiVersionSetInformationFile file = ApiVersionSetInformationFile.From(apiDirectory);
        JsonObject json = ApiVersionSet.Serialize(apiVersionSet);

        await file.OverwriteWithJson(json, cancellationToken);
    }

    private async ValueTask ExportApis(CancellationToken cancellationToken)
    {
        IAsyncEnumerable<common.Models.Api> apis =
            configurationModel.ApiDisplayNames is not null
            ? Api.List(getResources, serviceProviderUri, serviceName, configurationModel.ApiDisplayNames, cancellationToken)
            : Api.List(getResources, serviceProviderUri, serviceName, cancellationToken);

        await Parallel.ForEachAsync(apis, cancellationToken, ExportApi);
    }

    private async ValueTask ExportApi(common.Models.Api api, CancellationToken cancellationToken)
    {
        await ExportApiInformation(api, cancellationToken);
        await ExportApiPolicy(api, cancellationToken);
        await ExportApiContractFile(api, cancellationToken);
        await ExportApiDiagnostics(api, cancellationToken);
        await ExportApiOperations(api, cancellationToken);
    }

    private async ValueTask ExportApiInformation(common.Models.Api api, CancellationToken cancellationToken)
    {
        logger.LogInformation("Exporting information for api {apiName}...", api.Name);

        ApisDirectory apisDirectory = ApisDirectory.From(serviceDirectory);
        ApiDisplayName apiDisplayName = ApiDisplayName.From(api.Properties.DisplayName);
        ApiVersion apiVersion = ApiVersion.From(api.Properties.ApiVersion);
        ApiRevision apiRevision = ApiRevision.From(api.Properties.ApiRevision);

        ApiDirectory apiDirectory = ApiDirectory.From(apisDirectory, apiDisplayName, apiVersion, apiRevision);
        ApiInformationFile file = ApiInformationFile.From(apiDirectory);
        JsonObject json = Api.Serialize(api);

        await file.OverwriteWithJson(json, cancellationToken);
    }

    private async ValueTask ExportApiPolicy(common.Models.Api api, CancellationToken cancellationToken)
    {
        ApiName apiName = ApiName.From(api.Name);
        string? policyText = await ApiPolicy.TryGet(tryGetResource, serviceProviderUri, serviceName, apiName, cancellationToken);

        if (policyText is not null)
        {
            logger.LogInformation("Exporting policy for api {apiName}...", api.Name);

            ApisDirectory apisDirectory = ApisDirectory.From(serviceDirectory);
            ApiDisplayName apiDisplayName = ApiDisplayName.From(api.Properties.DisplayName);
            ApiVersion apiVersion = ApiVersion.From(api.Properties.ApiVersion);
            ApiRevision apiRevision = ApiRevision.From(api.Properties.ApiRevision);
            ApiDirectory apiDirectory = ApiDirectory.From(apisDirectory, apiDisplayName, apiVersion, apiRevision);
            ApiPolicyFile file = ApiPolicyFile.From(apiDirectory);

            await file.OverwriteWithText(policyText, cancellationToken);
        }
    }

    private ValueTask ExportApiContractFile(common.Models.Api api, CancellationToken cancellationToken)
    {
        logger.LogInformation("Exporting specification/schema for api {apiName}...", api.Name);

        ApisDirectory apisDirectory = ApisDirectory.From(serviceDirectory);
        ApiDisplayName apiDisplayName = ApiDisplayName.From(api.Properties.DisplayName);
        ApiVersion apiVersion = ApiVersion.From(api.Properties.ApiVersion);
        ApiRevision apiRevision = ApiRevision.From(api.Properties.ApiRevision);
        ApiDirectory apiDirectory = ApiDirectory.From(apisDirectory, apiDisplayName, apiVersion, apiRevision);
        ApiName apiName = ApiName.From(api.Name);

        return api.Properties.Type switch
        {
            common.Models.ApiType.graphql => ExportApiSpecification(apiDirectory, apiName, cancellationToken),
            _ => ExportApiGraphQLSchema(apiDirectory, apiName, cancellationToken)
        };
    }

    private async ValueTask ExportApiSpecification(ApiDirectory apiDirectory, ApiName apiName, CancellationToken cancellationToken)
    {
        ApiSpecificationFile file = ApiSpecificationFile.From(apiDirectory, apiSpecification);

        Func<Uri, CancellationToken, ValueTask<System.IO.Stream>> downloader = nonAuthenticatedHttpClient.GetSuccessfulResponseStream;
        using System.IO.Stream specificationStream = await ApiSpecification.Get(getResource, downloader, serviceProviderUri, serviceName, apiName, apiSpecification, cancellationToken);
        await file.OverwriteWithStream(specificationStream, cancellationToken);
    }

    private async ValueTask ExportApiGraphQLSchema(ApiDirectory apiDirectory, ApiName apiName, CancellationToken cancellationToken)
    {
        string? schemaText = await ApiSchema.TryGetGraphQLSchemaContent(tryGetResource, serviceProviderUri, serviceName, apiName, cancellationToken);

        if (schemaText is not null)
        {
            GraphQLSchemaFile file = GraphQLSchemaFile.From(apiDirectory);
            await file.OverwriteWithText(schemaText, cancellationToken);
        }
    }

    private async ValueTask ExportApiDiagnostics(common.Models.Api api, CancellationToken cancellationToken)
    {
        ApiName apiName = ApiName.From(api.Name);
        IAsyncEnumerable<common.Models.ApiDiagnostic> diagnostics = ApiDiagnostic.List(getResources, serviceProviderUri, serviceName, apiName, cancellationToken);

        await Parallel.ForEachAsync(diagnostics,
                                    cancellationToken,
                                    (diagnostic, cancellationToken) => ExportApiDiagnostic(api, diagnostic, cancellationToken));
    }

    private async ValueTask ExportApiDiagnostic(common.Models.Api api, common.Models.ApiDiagnostic diagnostic, CancellationToken cancellationToken)
    {
        logger.LogInformation("Exporting diagnostic {apiDiagnostic}for api {apiName}...", diagnostic.Name, api.Name);

        ApisDirectory apisDirectory = ApisDirectory.From(serviceDirectory);
        ApiDisplayName apiDisplayName = ApiDisplayName.From(api.Properties.DisplayName);
        ApiVersion apiVersion = ApiVersion.From(api.Properties.ApiVersion);
        ApiRevision apiRevision = ApiRevision.From(api.Properties.ApiRevision);
        ApiDirectory apiDirectory = ApiDirectory.From(apisDirectory, apiDisplayName, apiVersion, apiRevision);
        ApiDiagnosticsDirectory apiDiagnosticsDirectory = ApiDiagnosticsDirectory.From(apiDirectory);
        ApiDiagnosticName apiDiagnosticName = ApiDiagnosticName.From(diagnostic.Name); ;
        ApiDiagnosticDirectory apiDiagnosticDirectory = ApiDiagnosticDirectory.From(apiDiagnosticsDirectory, apiDiagnosticName);
        ApiDiagnosticInformationFile file = ApiDiagnosticInformationFile.From(apiDiagnosticDirectory);
        JsonObject json = ApiDiagnostic.Serialize(diagnostic);

        await file.OverwriteWithJson(json, cancellationToken);
    }

    private async ValueTask ExportApiOperations(common.Models.Api api, CancellationToken cancellationToken)
    {
        ApiName apiName = ApiName.From(api.Name);
        IAsyncEnumerable<common.Models.ApiOperation> apiOperations = ApiOperation.List(getResources, serviceProviderUri, serviceName, apiName, cancellationToken);

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
        ApiName apiName = ApiName.From(api.Name);
        ApiOperationName apiOperationName = ApiOperationName.From(apiOperation.Name);
        string? policyText = await ApiOperationPolicy.TryGet(tryGetResource, serviceProviderUri, serviceName, apiName, apiOperationName, cancellationToken);

        if (policyText is not null)
        {
            logger.LogInformation("Exporting policy for apiOperation {apiOperationName} in api {apiName}...", apiOperation.Name, api.Name);

            ApisDirectory apisDirectory = ApisDirectory.From(serviceDirectory);
            ApiDisplayName apiDisplayName = ApiDisplayName.From(api.Properties.DisplayName);
            ApiVersion apiVersion = ApiVersion.From(api.Properties.ApiVersion);
            ApiRevision apiRevision = ApiRevision.From(api.Properties.ApiRevision);
            ApiDirectory apiDirectory = ApiDirectory.From(apisDirectory, apiDisplayName, apiVersion, apiRevision);
            ApiOperationsDirectory apiOperationsDirectory = ApiOperationsDirectory.From(apiDirectory);
            ApiOperationDisplayName apiOperationDisplayName = ApiOperationDisplayName.From(apiOperation.Properties.DisplayName);
            ApiOperationDirectory apiOperationDirectory = ApiOperationDirectory.From(apiOperationsDirectory, apiOperationDisplayName);
            ApiOperationPolicyFile file = ApiOperationPolicyFile.From(apiOperationDirectory);

            await file.OverwriteWithText(policyText, cancellationToken);
        }
    }
}