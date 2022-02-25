namespace extractor;

internal class Extractor : ConsoleService
{
    private readonly ArmClient armClient;
    private readonly NonAuthenticatedHttpClient nonAuthenticatedHttpClient;
    private readonly ServiceUri serviceUri;
    private readonly DirectoryInfo serviceOutputDirectory;
    private readonly ApiSpecificationFormat apiSpecificationFormat;

    public Extractor(IHostApplicationLifetime applicationLifetime, ILogger<Extractor> logger, IConfiguration configuration, ArmClient armClient, NonAuthenticatedHttpClient nonAuthenticatedHttpClient) : base(applicationLifetime, logger)
    {
        this.armClient = armClient;
        this.nonAuthenticatedHttpClient = nonAuthenticatedHttpClient;
        serviceUri = GetServiceUri(configuration, armClient);
        serviceOutputDirectory = GetOutputDirectory(configuration);
        apiSpecificationFormat = GetApiSpecificationFormat(configuration);
    }

    private static ServiceUri GetServiceUri(IConfiguration configuration, ArmClient armClient)
    {
        var subscriptionId = configuration["AZURE_SUBSCRIPTION_ID"];
        var resourceGroupName = configuration["AZURE_RESOURCE_GROUP_NAME"];
        var serviceName = configuration["API_MANAGEMENT_SERVICE_NAME"];

        var serviceUri = armClient.GetBaseUri()
                                  .AppendPath("subscriptions")
                                  .AppendPath(subscriptionId)
                                  .AppendPath("resourceGroups")
                                  .AppendPath(resourceGroupName)
                                  .AppendPath("providers/Microsoft.ApiManagement/service")
                                  .AppendPath(serviceName)
                                  .SetQueryParameter("api-version", "2021-04-01-preview");

        return ServiceUri.From(serviceUri);
    }

    private static DirectoryInfo GetOutputDirectory(IConfiguration configuration)
    {
        var path = configuration["API_MANAGEMENT_SERVICE_OUTPUT_FOLDER_PATH"];

        return new DirectoryInfo(path);
    }

    private static ApiSpecificationFormat GetApiSpecificationFormat(IConfiguration configuration)
    {
        var section = configuration.GetSection("API_SPECIFICATION_FORMAT");

        return section.Exists() && Enum.TryParse<ApiSpecificationFormat>(section.Value, out var apiSpecificationFormat)
            ? apiSpecificationFormat
            : ApiSpecificationFormat.Yaml;
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        return Task.WhenAll(ExportServiceInformation(cancellationToken),
                            TryExportServicePolicy(cancellationToken),
                            ExportProducts(cancellationToken),
                            ExportGateways(cancellationToken),
                            ExportAuthorizationServers(cancellationToken),
                            ExportServiceDiagnostics(cancellationToken),
                            ExportLoggers(cancellationToken),
                            ExportApis(cancellationToken));
    }

    private async Task<Unit> ExportServiceInformation(CancellationToken cancellationToken)
    {
        var serviceInformationJson = await armClient.GetResource(serviceUri, cancellationToken)
                                                    .Map(ServiceInformation.FormatResponseJson);

        var serviceInformationFile = serviceOutputDirectory.GetFileInfo(Constants.ServiceInformationFileName);

        return await serviceInformationFile.OverwriteWithJson(serviceInformationJson, cancellationToken);
    }

    private Task<Unit> TryExportServicePolicy(CancellationToken cancellationToken)
    {
        var policiesUri = common.Policy.GetListByServiceUri(serviceUri);

        return TryExportPolicy(policiesUri, serviceOutputDirectory, cancellationToken);
    }

    private Task<Unit> TryExportPolicy(Uri policiesUri, DirectoryInfo policyOutputDirectory, CancellationToken cancellationToken)
    {
        var exportPolicy = (string policy) => policyOutputDirectory.GetFileInfo(Constants.PolicyFileName)
                                                                   .OverwriteWithText(policy, cancellationToken);

        return armClient.GetResources(policiesUri, cancellationToken)
                .TryPick(common.Policy.TryGetFromResponseJson, cancellationToken)
                .Bind(policy => policy.Map(exportPolicy)
                                      .IfNull(() => Task.FromResult(Unit.Default)));
    }

    private Task<Unit> ExportProducts(CancellationToken cancellationToken)
    {
        var productsUri = Product.GetListByServiceUri(serviceUri);
        var productsOutputDirectory = serviceOutputDirectory.GetSubDirectory(Constants.ProductsFolderName);
        var productJsons = armClient.GetResources(productsUri, cancellationToken);

        return productJsons.Select(productJson => productJson.GetNonEmptyStringPropertyValue("name"))
                           .Select(ProductName.From)
                           .Select(productName => Product.GetUri(serviceUri, productName))
                           .ExecuteInParallel(productUri => ExportProduct(productUri, productsOutputDirectory, cancellationToken), cancellationToken);
    }

    private async Task<Unit> ExportProduct(ProductUri productUri, DirectoryInfo productsOutputDirectory, CancellationToken cancellationToken)
    {
        var productInformationJson = await armClient.GetResource(productUri, cancellationToken)
                                                    .Map(ProductInformation.FormatResponseJson)
                                                    .Bind(async productJson =>
                                                    {
                                                        var apis = await GetProductApis(productUri, cancellationToken);
                                                        return productJson.AddProperty("apis", apis);
                                                    });

        var productDisplayName = GetDisplayNameFromResourceJson(productInformationJson);
        var productOutputDirectory = productsOutputDirectory.GetSubDirectory(DirectoryName.From(productDisplayName));

        await Task.WhenAll(ExportProductInformation(productInformationJson, productOutputDirectory, cancellationToken),
                           TryExportProductPolicy(productUri, productOutputDirectory, cancellationToken));

        return Unit.Default;
    }

    private static string GetDisplayNameFromResourceJson(JsonObject json)
    {
        return json.GetObjectPropertyValue("properties")
                   .GetNonEmptyStringPropertyValue("displayName");
    }

    private Task<JsonArray> GetProductApis(ProductUri productUri, CancellationToken cancellationToken)
    {
        var apisUri = Api.GetListByProductUri(productUri);
        var apiJsons = armClient.GetResources(apisUri, cancellationToken);

        return apiJsons.Select(GetDisplayNameFromResourceJson)
                       .Select(apiDisplayName => new JsonObject().AddStringProperty("displayName", apiDisplayName))
                       .ToListAsync(cancellationToken)
                       .AsTask()
                       .Map(apiJsons => apiJsons.ToJsonArray());
    }

    private static Task<Unit> ExportProductInformation(JsonObject productInformationJson, DirectoryInfo productOutputDirectory, CancellationToken cancellationToken)
    {
        var productInformationFile = productOutputDirectory.GetFileInfo(Constants.ProductInformationFileName);
        return productInformationFile.OverwriteWithJson(productInformationJson, cancellationToken);
    }

    private Task<Unit> TryExportProductPolicy(ProductUri productUri, DirectoryInfo productOutputDirectory, CancellationToken cancellationToken)
    {
        var policiesUri = common.Policy.GetListByProductUri(productUri);

        return TryExportPolicy(policiesUri, productOutputDirectory, cancellationToken);
    }

    private Task<Unit> ExportGateways(CancellationToken cancellationToken)
    {
        var gatewaysUri = Gateway.GetListByServiceUri(serviceUri);
        var gatewaysOutputDirectory = serviceOutputDirectory.GetSubDirectory(Constants.GatewaysFolderName);
        var gatewayJsons = armClient.GetResources(gatewaysUri, cancellationToken);

        return gatewayJsons.Select(gatewayJson => gatewayJson.GetNonEmptyStringPropertyValue("name"))
                           .Select(GatewayName.From)
                           .Select(gatewayName => Gateway.GetUri(serviceUri, gatewayName))
                           .ExecuteInParallel(gatewayUri => ExportGateway(gatewayUri, gatewaysOutputDirectory, cancellationToken), cancellationToken);
    }

    private async Task<Unit> ExportGateway(GatewayUri gatewayUri, DirectoryInfo gatewaysOutputDirectory, CancellationToken cancellationToken)
    {
        var gatewayInformationJson = await armClient.GetResource(gatewayUri, cancellationToken)
                                                    .Map(GatewayInformation.FormatResponseJson)
                                                    .Bind(async gatewayJson =>
                                                    {
                                                        var apis = await GetGatewayApis(gatewayUri, cancellationToken);
                                                        return gatewayJson.AddProperty("apis", apis);
                                                    });

        var gatewayName = gatewayInformationJson.GetNonEmptyStringPropertyValue("name");
        var gatewayOutputDirectory = gatewaysOutputDirectory.GetSubDirectory(DirectoryName.From(gatewayName));

        return await ExportGatewayInformation(gatewayInformationJson, gatewayOutputDirectory, cancellationToken);
    }

    private Task<JsonArray> GetGatewayApis(GatewayUri gatewayUri, CancellationToken cancellationToken)
    {
        var apisUri = Api.GetListByGatewayUri(gatewayUri);
        var apiJsons = armClient.GetResources(apisUri, cancellationToken);

        return apiJsons.Select(GetDisplayNameFromResourceJson)
                       .Select(apiDisplayName => new JsonObject().AddStringProperty("displayName", apiDisplayName))
                       .ToListAsync(cancellationToken)
                       .AsTask()
                       .Map(apiJsons => apiJsons.ToJsonArray());
    }

    private static Task<Unit> ExportGatewayInformation(JsonObject gatewayInformationJson, DirectoryInfo gatewayOutputDirectory, CancellationToken cancellationToken)
    {
        var gatewayInformationFile = gatewayOutputDirectory.GetFileInfo(Constants.GatewayInformationFileName);
        return gatewayInformationFile.OverwriteWithJson(gatewayInformationJson, cancellationToken);
    }

    private Task<Unit> ExportAuthorizationServers(CancellationToken cancellationToken)
    {
        var authorizationServersUri = AuthorizationServer.GetListByServiceUri(serviceUri);
        var authorizationServersOutputDirectory = serviceOutputDirectory.GetSubDirectory(Constants.AuthorizationServersFolderName);
        var authorizationServerJsons = armClient.GetResources(authorizationServersUri, cancellationToken);

        return authorizationServerJsons.Select(authorizationServerJson => authorizationServerJson.GetNonEmptyStringPropertyValue("name"))
                                       .Select(AuthorizationServerName.From)
                                       .Select(authorizationServerName => AuthorizationServer.GetUri(serviceUri, authorizationServerName))
                                       .ExecuteInParallel(authorizationServerUri => ExportAuthorizationServer(authorizationServerUri, authorizationServersOutputDirectory, cancellationToken), cancellationToken);
    }

    private async Task<Unit> ExportAuthorizationServer(AuthorizationServerUri authorizationServerUri, DirectoryInfo authorizationServersOutputDirectory, CancellationToken cancellationToken)
    {
        var authorizationServerInformationJson = await armClient.GetResource(authorizationServerUri, cancellationToken)
                                                                .Map(AuthorizationServerInformation.FormatResponseJson);

        var authorizationServerName = authorizationServerInformationJson.GetNonEmptyStringPropertyValue("name");

        var authorizationServerOutputDirectory = authorizationServersOutputDirectory.GetSubDirectory(DirectoryName.From(authorizationServerName));

        return await ExportAuthorizationServerInformation(authorizationServerInformationJson, authorizationServerOutputDirectory, cancellationToken);
    }

    private static Task<Unit> ExportAuthorizationServerInformation(JsonObject authorizationServerInformationJson, DirectoryInfo authorizationServerOutputDirectory, CancellationToken cancellationToken)
    {
        var authorizationServerInformationFile = authorizationServerOutputDirectory.GetFileInfo(Constants.AuthorizationServerInformationFileName);
        return authorizationServerInformationFile.OverwriteWithJson(authorizationServerInformationJson, cancellationToken);
    }

    private Task<Unit> ExportServiceDiagnostics(CancellationToken cancellationToken)
    {
        var diagnosticsUri = Diagnostic.GetListByServiceUri(serviceUri);
        var diagnosticsOutputDirectory = serviceOutputDirectory.GetSubDirectory(Constants.DiagnosticsFolderName);
        var diagnosticJsons = armClient.GetResources(diagnosticsUri, cancellationToken);

        return ExportDiagnostics(diagnosticJsons,
                                 getDiagnosticUriFromName: diagnosticName => Diagnostic.GetUri(serviceUri, diagnosticName),
                                 diagnosticsOutputDirectory,
                                 cancellationToken);
    }

    private Task<Unit> ExportDiagnostics(IAsyncEnumerable<JsonObject> diagnosticJsons, Func<DiagnosticName, DiagnosticUri> getDiagnosticUriFromName, DirectoryInfo diagnosticsOutputDirectory, CancellationToken cancellationToken)
    {
        return diagnosticJsons.Select(diagnosticJson => diagnosticJson.GetNonEmptyStringPropertyValue("name"))
                              .Select(DiagnosticName.From)
                              .Select(getDiagnosticUriFromName)
                              .ExecuteInParallel(diagnosticUri => ExportDiagnostic(diagnosticUri, diagnosticsOutputDirectory, cancellationToken), cancellationToken);
    }

    private async Task<Unit> ExportDiagnostic(DiagnosticUri diagnosticUri, DirectoryInfo diagnosticsOutputDirectory, CancellationToken cancellationToken)
    {
        var diagnosticInformationJson = await armClient.GetResource(diagnosticUri, cancellationToken)
                                                       .Map(DiagnosticInformation.FormatResponseJson);

        var diagnosticName = diagnosticInformationJson.GetNonEmptyStringPropertyValue("name");

        var diagnosticOutputDirectory = diagnosticsOutputDirectory.GetSubDirectory(DirectoryName.From(diagnosticName));

        return await ExportDiagnosticInformation(diagnosticInformationJson, diagnosticOutputDirectory, cancellationToken);
    }

    private static Task<Unit> ExportDiagnosticInformation(JsonObject diagnosticInformationJson, DirectoryInfo diagnosticOutputDirectory, CancellationToken cancellationToken)
    {
        var diagnosticInformationFile = diagnosticOutputDirectory.GetFileInfo(Constants.DiagnosticInformationFileName);
        return diagnosticInformationFile.OverwriteWithJson(diagnosticInformationJson, cancellationToken);
    }

    private Task<Unit> ExportLoggers(CancellationToken cancellationToken)
    {
        var loggersUri = common.Logger.GetListByServiceUri(serviceUri);
        var loggersOutputDirectory = serviceOutputDirectory.GetSubDirectory(Constants.LoggersFolderName);
        var loggerJsons = armClient.GetResources(loggersUri, cancellationToken);

        return loggerJsons.Select(loggerJson => loggerJson.GetNonEmptyStringPropertyValue("name"))
                          .Select(LoggerName.From)
                          .Select(loggerName => common.Logger.GetUri(serviceUri, loggerName))
                          .ExecuteInParallel(loggerUri => ExportLogger(loggerUri, loggersOutputDirectory, cancellationToken), cancellationToken);
    }

    private async Task<Unit> ExportLogger(LoggerUri loggerUri, DirectoryInfo loggersOutputDirectory, CancellationToken cancellationToken)
    {
        var loggerInformationJson = await armClient.GetResource(loggerUri, cancellationToken)
                                                   .Map(LoggerInformation.FormatResponseJson);

        var loggerName = loggerInformationJson.GetNonEmptyStringPropertyValue("name");

        var loggerOutputDirectory = loggersOutputDirectory.GetSubDirectory(DirectoryName.From(loggerName));

        return await ExportLoggerInformation(loggerInformationJson, loggerOutputDirectory, cancellationToken);
    }

    private static Task<Unit> ExportLoggerInformation(JsonObject loggerInformationJson, DirectoryInfo loggerOutputDirectory, CancellationToken cancellationToken)
    {
        var loggerInformationFile = loggerOutputDirectory.GetFileInfo(Constants.LoggerInformationFileName);
        return loggerInformationFile.OverwriteWithJson(loggerInformationJson, cancellationToken);
    }

    private Task<Unit> ExportApis(CancellationToken cancellationToken)
    {
        var apisUri = Api.GetListByServiceUri(serviceUri);
        var apisOutputDirectory = serviceOutputDirectory.GetSubDirectory(Constants.ApisFolderName);
        var apiJsons = armClient.GetResources(apisUri, cancellationToken);

        return apiJsons.Select(apiJson => apiJson.GetNonEmptyStringPropertyValue("name"))
                       .Select(ApiName.From)
                       .Select(apiName => Api.GetUri(serviceUri, apiName))
                       .ExecuteInParallel(apiUri => ExportApi(apiUri, apisOutputDirectory, cancellationToken), cancellationToken);
    }

    private async Task<Unit> ExportApi(ApiUri apiUri, DirectoryInfo apisOutputDirectory, CancellationToken cancellationToken)
    {
        var apiInformationJson = await armClient.GetResource(apiUri, cancellationToken)
                                                .Map(ApiInformation.FormatResponseJson);

        var apiDisplayName = GetDisplayNameFromResourceJson(apiInformationJson);
        var apiOutputDirectory = apisOutputDirectory.GetSubDirectory(DirectoryName.From(apiDisplayName));

        await Task.WhenAll(ExportApiInformation(apiInformationJson, apiOutputDirectory, cancellationToken),
                                  ExportApiSpecification(apiUri, apiOutputDirectory, cancellationToken),
                                  ExportApiDiagnostics(apiUri, apiOutputDirectory, cancellationToken),
                                  TryExportApiPolicy(apiUri, apiOutputDirectory, cancellationToken),
                                  ExportOperations(apiUri, apiOutputDirectory, cancellationToken));

        return Unit.Default;
    }

    private static Task<Unit> ExportApiInformation(JsonObject apiInformationJson, DirectoryInfo apiOutputDirectory, CancellationToken cancellationToken)
    {
        var apiInformationFile = apiOutputDirectory.GetFileInfo(Constants.ApiInformationFileName);
        return apiInformationFile.OverwriteWithJson(apiInformationJson, cancellationToken);
    }

    private async Task<Unit> ExportApiSpecification(ApiUri apiUri, DirectoryInfo apiOutputDirectory, CancellationToken cancellationToken)
    {
        var specificationUri = ApiSpecification.GetExportUri(apiUri, apiSpecificationFormat);
        using var specificationStream = await armClient.GetResource(specificationUri, cancellationToken)
                                                       .Map(ApiSpecification.GetDownloadUriFromResponse)
                                                       .Bind(downloadUri => nonAuthenticatedHttpClient.GetSuccessfulResponseStream(downloadUri, cancellationToken));

        var specificationFileName = apiSpecificationFormat switch
        {
            ApiSpecificationFormat.Json => Constants.ApiJsonSpecificationFileName,
            _ => Constants.ApiYamlSpecificationFileName
        };

        var specificationFile = apiOutputDirectory.GetFileInfo(specificationFileName);
        return await specificationFile.OverwriteWithStream(specificationStream, cancellationToken);
    }

    private Task<Unit> ExportApiDiagnostics(ApiUri apiUri, DirectoryInfo apiOutputDirectory, CancellationToken cancellationToken)
    {
        var diagnosticsUri = Diagnostic.GetListByApiUri(apiUri);
        var diagnosticsOutputDirectory = apiOutputDirectory.GetSubDirectory(Constants.DiagnosticsFolderName);
        var diagnosticJsons = armClient.GetResources(diagnosticsUri, cancellationToken);
        var getDiagnosticUriFromName = (DiagnosticName diagnosticName) => Diagnostic.GetUri(apiUri, diagnosticName);

        return ExportDiagnostics(diagnosticJsons,
                                 getDiagnosticUriFromName: diagnosticName => Diagnostic.GetUri(apiUri, diagnosticName),
                                 diagnosticsOutputDirectory,
                                 cancellationToken);
    }

    private Task<Unit> TryExportApiPolicy(ApiUri apiUri, DirectoryInfo apiOutputDirectory, CancellationToken cancellationToken)
    {
        var policiesUri = common.Policy.GetListByApiUri(apiUri);

        return TryExportPolicy(policiesUri, apiOutputDirectory, cancellationToken);
    }

    private Task<Unit> ExportOperations(ApiUri apiUri, DirectoryInfo apiOutputDirectory, CancellationToken cancellationToken)
    {
        var operationsUri = Operation.GetListByApiUri(apiUri);
        var operationsOutputDirectory = apiOutputDirectory.GetSubDirectory(Constants.OperationsFolderName);
        var operationJsons = armClient.GetResources(operationsUri, cancellationToken);

        return operationJsons.Select(operationJson => operationJson.GetNonEmptyStringPropertyValue("name"))
                             .Select(OperationName.From)
                             .Select(operationName => Operation.GetUri(apiUri, operationName))
                             .ExecuteInParallel(operationUri => ExportOperation(operationUri, operationsOutputDirectory, cancellationToken), cancellationToken);
    }

    private async Task<Unit> ExportOperation(OperationUri operationUri, DirectoryInfo operationsOutputDirectory, CancellationToken cancellationToken)
    {
        var operationInformationJson = await armClient.GetResource(operationUri, cancellationToken);

        var operationDisplayName = GetDisplayNameFromResourceJson(operationInformationJson);
        var operationOutputDirectory = operationsOutputDirectory.GetSubDirectory(DirectoryName.From(operationDisplayName));

        return await TryExportOperationPolicy(operationUri, operationOutputDirectory, cancellationToken);
    }

    private Task<Unit> TryExportOperationPolicy(OperationUri operationUri, DirectoryInfo operationOutputDirectory, CancellationToken cancellationToken)
    {
        var policiesUri = common.Policy.GetListByOperationUri(operationUri);

        return TryExportPolicy(policiesUri, operationOutputDirectory, cancellationToken);
    }
}
