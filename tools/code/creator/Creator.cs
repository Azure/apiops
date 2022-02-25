namespace creator;

public class Creator : ConsoleService
{
    private readonly ServiceUri serviceUri;
    private readonly DirectoryInfo serviceDirectory;
    private readonly CommitId? commitId;
    private readonly Func<Uri, CancellationToken, IAsyncEnumerable<JsonObject>> getResources;
    private readonly Func<Uri, Stream, CancellationToken, Task<Unit>> putResource;
    private readonly Func<Uri, CancellationToken, Task<Unit>> deleteResource;

    public Creator(IHostApplicationLifetime applicationLifetime, ILogger<Creator> logger, IConfiguration configuration, ArmClient armClient) : base(applicationLifetime, logger)
    {
        this.serviceDirectory = GetServiceDirectory(configuration);
        this.serviceUri = GetServiceUri(configuration, armClient, serviceDirectory);
        this.commitId = TryGetCommitId(configuration);
        this.getResources = armClient.GetResources;
        this.putResource = armClient.PutResource;
        this.deleteResource = armClient.DeleteResource;
    }

    private static DirectoryInfo GetServiceDirectory(IConfiguration configuration)
    {
        var path = configuration["API_MANAGEMENT_SERVICE_OUTPUT_FOLDER_PATH"];

        return new DirectoryInfo(path);
    }

    private static ServiceUri GetServiceUri(IConfiguration configuration, ArmClient armClient, DirectoryInfo serviceDirectory)
    {
        var subscriptionId = configuration["AZURE_SUBSCRIPTION_ID"];
        var resourceGroupName = configuration["AZURE_RESOURCE_GROUP_NAME"];
        var serviceInformationFile = serviceDirectory.GetFileInfo(Constants.ServiceInformationFileName);
        var serviceName = Service.GetNameFromInformationFile(serviceInformationFile, CancellationToken.None).GetAwaiter().GetResult();

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

    private static CommitId? TryGetCommitId(IConfiguration configuration)
    {
        var configurationSection = configuration.GetSection("COMMIT_ID");

        return configurationSection.Exists()
            ? CommitId.From(configurationSection.Value)
            : null;
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        await GetFilesToProcess().Map(ClassifyFiles)
                                 .Bind(fileMap => ProcessFiles(fileMap, cancellationToken));
    }

    private Task<ILookup<ResourceAction, FileInfo>> GetFilesToProcess()
    {
        var matchCommitStatusToAction = (CommitStatus status) => status switch
            {
                CommitStatus.Delete => ResourceAction.Delete,
                _ => ResourceAction.Put
            };

        var getLookupFromCommitId = (CommitId commitId) =>
            Git.GetFilesFromCommit(commitId, serviceDirectory)
               .Map(lookup => lookup.MapKeys(matchCommitStatusToAction));

        var getLookupFromDirectory = () =>
            serviceDirectory.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true })
                            .ToLookup(_ => ResourceAction.Put);

        return commitId.Map(getLookupFromCommitId)
                       .IfNull(() => Task.FromResult(getLookupFromDirectory()));
    }

    private ImmutableDictionary<ResourceAction, ILookup<FileType, FileInfo>> ClassifyFiles(ILookup<ResourceAction, FileInfo> fileLookup)
    {
        return fileLookup.ToImmutableDictionary(grouping => grouping.Key,
                                                grouping => grouping.ToLookup(file => FileType.TryGetFileType(serviceDirectory, file))
                                                                    .RemoveNullKeys());
    }

    private async Task<Unit> ProcessFiles(ImmutableDictionary<ResourceAction, ILookup<FileType, FileInfo>> fileMap, CancellationToken cancellationToken)
    {
        foreach (var (resourceAction, fileLookup) in fileMap)
        {
            await ProcessFiles(resourceAction, fileLookup, cancellationToken);
        }

        return Unit.Default;
    }

    private Task<Unit> ProcessFiles(ResourceAction resourceAction, ILookup<FileType, FileInfo> fileLookup, CancellationToken cancellationToken)
    {
        return resourceAction switch
        {
            ResourceAction.Put => ProcessFilesToPut(fileLookup, cancellationToken),
            ResourceAction.Delete => ProcessFilesToDelete(fileLookup, cancellationToken),
            _ => throw new InvalidOperationException($"Resource action {resourceAction} is invalid.")
        };
    }

    private async Task<Unit> ProcessFilesToPut(ILookup<FileType, FileInfo> fileLookup, CancellationToken cancellationToken)
    {
        var putServiceInformationFile = () => fileLookup.Lookup(FileType.ServiceInformation)
                                                        .FirstOrDefault()
                                                        .Map(file => PutServiceInformation(file, cancellationToken))
                                                        .IfNull(() => Task.FromResult(Unit.Default));

        var putAuthorizationServers = () => fileLookup.Lookup(FileType.AuthorizationServerInformation)
                                                      .ExecuteInParallel(PutAuthorizationServerInformation, cancellationToken);

        var putGateways = () => fileLookup.Lookup(FileType.GatewayInformation)
                                          .ExecuteInParallel(PutGatewayInformation, cancellationToken);

        var putServicePolicy = () => fileLookup.Lookup(FileType.ServicePolicy)
                                                .FirstOrDefault()
                                                .Map(file => PutServicePolicy(file, cancellationToken))
                                                .IfNull(() => Task.FromResult(Unit.Default));

        var putProducts = () => fileLookup.Lookup(FileType.ProductInformation)
                                          .ExecuteInParallel(PutProductInformation, cancellationToken);

        var putProductPolicies = () => fileLookup.Lookup(FileType.ProductPolicy)
                                                 .ExecuteInParallel(PutProductPolicy, cancellationToken);

        var putLoggers = () => fileLookup.Lookup(FileType.LoggerInformation)
                                         .ExecuteInParallel(PutLoggerInformation, cancellationToken);

        var putServiceDiagnostics = () => fileLookup.Lookup(FileType.ServiceDiagnosticInformation)
                                                    .ExecuteInParallel(PutServiceDiagnosticInformation, cancellationToken);

        var putApiInformation = () =>
        {
            var getInformationFileFromSpecificationFile = (FileInfo specificationFile) => specificationFile.GetDirectoryInfo()
                                                                                                           .GetFileInfo(Constants.ApiInformationFileName);

            var jsonSpecificationFiles = fileLookup.Lookup(FileType.ApiJsonSpecification);
            var yamlSpecificationFiles = fileLookup.Lookup(FileType.ApiYamlSpecification);
            var specificationFiles = jsonSpecificationFiles.Concat(yamlSpecificationFiles);
            var apiInformationFiles = fileLookup.Lookup(FileType.ApiInformation);

            return specificationFiles.Select(getInformationFileFromSpecificationFile)
                                     .Concat(apiInformationFiles)
                                     .DistinctBy(file => file.FullName.Normalize())
                                     .ExecuteInParallel(PutApiInformation, cancellationToken);
        };

        var putApiDiagnostics = () => fileLookup.Lookup(FileType.ApiDiagnosticInformation)
                                                .ExecuteInParallel(PutApiDiagnosticInformation, cancellationToken);

        var putApiPolicies = () => fileLookup.Lookup(FileType.ApiPolicy)
                                             .ExecuteInParallel(PutApiPolicy, cancellationToken);

        var putOperationPolicies = () => fileLookup.Lookup(FileType.OperationPolicy)
                                                   .ExecuteInParallel(PutOperationPolicy, cancellationToken);

        await putServiceInformationFile();

        await Task.WhenAll(putAuthorizationServers(),
                           putGateways(),
                           putServicePolicy(),
                           putProducts(),
                           putLoggers());

        await Task.WhenAll(putProductPolicies(), putServiceDiagnostics());

        await putApiInformation();

        await Task.WhenAll(putApiPolicies(), putApiDiagnostics(), putOperationPolicies());

        return Unit.Default;
    }

    private async Task<Unit> PutServiceInformation(FileInfo file, CancellationToken cancellationToken)
    {
        Logger.LogInformation($"Updating Azure service information with file {file}...");

        using var stream = file.OpenRead();

        return await putResource(serviceUri, stream, cancellationToken);
    }

    private async Task<Unit> PutAuthorizationServerInformation(FileInfo file, CancellationToken cancellationToken)
    {
        Logger.LogInformation($"Updating Azure service authorization server with file {file}...");

        using var stream = file.OpenRead();
        var authorizationServerName = await AuthorizationServer.GetNameFromInformationFile(file, cancellationToken);
        var authorizationServerUri = AuthorizationServer.GetUri(serviceUri, authorizationServerName);

        return await putResource(authorizationServerUri, stream, cancellationToken);
    }

    private async Task<Unit> PutGatewayInformation(FileInfo file, CancellationToken cancellationToken)
    {
        Logger.LogInformation($"Updating Azure service gateway with file {file}...");

        using var stream = file.OpenRead();
        var gatewayName = await Gateway.GetNameFromInformationFile(file, cancellationToken);
        var gatewayUri = Gateway.GetUri(serviceUri, gatewayName);

        await putResource(gatewayUri, stream, cancellationToken);

        return await SetGatewayApis(file, cancellationToken);
    }

    private async Task<Unit> SetGatewayApis(FileInfo file, CancellationToken cancellationToken)
    {
        var gatewayName = await Gateway.GetNameFromInformationFile(file, cancellationToken);
        var gatewayUri = Gateway.GetUri(serviceUri, gatewayName);
        var listApisUri = Api.GetListByGatewayUri(gatewayUri);

        var publishedApiDisplayNames = await getResources(listApisUri, cancellationToken).Select(jsonObject => jsonObject.GetObjectPropertyValue("properties")
                                                                                                                         .GetNonEmptyStringPropertyValue("displayName"))
                                                                                         .ToListAsync(cancellationToken);

        var fileApiDisplayNames = await file.ReadAsJsonObject(cancellationToken)
                                            .Map(json => json.TryGetObjectArrayPropertyValue("apis")
                                                             .IfNull(() => Enumerable.Empty<JsonObject>())
                                                             .Select(jsonObject => jsonObject.GetNonEmptyStringPropertyValue("displayName")));

        var gatewayApisToDelete = publishedApiDisplayNames.ExceptBy(fileApiDisplayNames, displayName => displayName.Normalize());
        var gatewayApisToCreate = fileApiDisplayNames.ExceptBy(publishedApiDisplayNames, displayName => displayName.Normalize());

        var deletionTasks = gatewayApisToDelete.Select(displayName => GetApiNameFromServiceUri(displayName, cancellationToken).Bind(apiName => DeleteGatewayApi(gatewayUri, apiName, cancellationToken)));
        var creationTasks = gatewayApisToCreate.Select(displayName => GetApiNameFromServiceDirectory(displayName, cancellationToken).Bind(apiName => PutGatewayApi(gatewayUri, apiName, cancellationToken)));

        await Task.WhenAll(deletionTasks.Concat(creationTasks));

        return Unit.Default;
    }

    private async Task<ApiName> GetApiNameFromServiceUri(string apiDisplayName, CancellationToken cancellationToken)
    {
        var apiListUri = Api.GetListByServiceUri(serviceUri);

        return await getResources(apiListUri, cancellationToken).Where(apiJson => apiDisplayName == GetDisplayNameFromResourceJson(apiJson))
                                                                .Select(apiJson => apiJson.GetNonEmptyStringPropertyValue("name"))
                                                                .Select(ApiName.From)
                                                                .FirstOrDefaultAsync(cancellationToken)
                            ?? throw new InvalidOperationException($"Could not find API with display name {apiDisplayName}.");
    }

    private Task<ApiName> GetApiNameFromServiceDirectory(string apiDisplayName, CancellationToken cancellationToken)
    {
        return serviceDirectory.GetSubDirectory(Constants.ApisFolderName)
                               .GetSubDirectory(DirectoryName.From(apiDisplayName))
                               .GetFileInfo(Constants.ApiInformationFileName)
                               .ReadAsJsonObject(cancellationToken)
                               .Map(jsonObject => jsonObject.GetNonEmptyStringPropertyValue("name"))
                               .Map(ApiName.From);
    }

    private async Task<Unit> DeleteGatewayApi(GatewayUri gatewayUri, ApiName apiName, CancellationToken cancellationToken)
    {
        var gatewayApiUri = Api.GetUri(gatewayUri, apiName);

        return await deleteResource(gatewayApiUri, cancellationToken);
    }

    private async Task<Unit> PutGatewayApi(GatewayUri gatewayUri, ApiName apiName, CancellationToken cancellationToken)
    {
        var gatewayApiUri = Api.GetUri(gatewayUri, apiName);

        using var payloadStream = new MemoryStream();

        var payloadJson = new JsonObject().AddProperty("properties",
                                                       new JsonObject().AddStringProperty("provisioningState", "created"));

        await payloadJson.SerializeToStream(payloadStream, cancellationToken);

        return await putResource(gatewayApiUri, payloadStream, cancellationToken);
    }

    private Task<Unit> PutServicePolicy(FileInfo file, CancellationToken cancellationToken)
    {
        Logger.LogInformation($"Updating Azure service policy with file {file}...");

        var policyUri = Policy.GetServicePolicyUri(serviceUri);

        return PutPolicy(policyUri, file, cancellationToken);
    }

    private async Task<Unit> PutPolicy(Uri policyUri, FileInfo file, CancellationToken cancellationToken)
    {
        var policyText = await file.ReadAsText(cancellationToken);

        using var stream = new MemoryStream();

        await new JsonObject().AddStringProperty("format", "rawxml")
                              .AddStringProperty("value", policyText)
                              .AddToJsonObject("properties", new JsonObject())
                              .SerializeToStream(stream, cancellationToken);

        return await putResource(policyUri, stream, cancellationToken);
    }

    private async Task<Unit> PutServiceDiagnosticInformation(FileInfo file, CancellationToken cancellationToken)
    {
        var diagnosticName = await Diagnostic.GetNameFromInformationFile(file, cancellationToken);
        var diagnosticUri = Diagnostic.GetUri(serviceUri, diagnosticName);
        using var fileStream = file.OpenRead();

        return await putResource(diagnosticUri, fileStream, cancellationToken);
    }

    private async Task<Unit> PutProductInformation(FileInfo file, CancellationToken cancellationToken)
    {
        Logger.LogInformation($"Updating Azure service product with file {file}...");

        using var stream = file.OpenRead();
        var productName = await Product.GetNameFromInformationFile(file, cancellationToken);
        var productUri = Product.GetUri(serviceUri, productName);

        await putResource(productUri, stream, cancellationToken);

        return await SetProductApis(file, cancellationToken);
    }

    private async Task<Unit> SetProductApis(FileInfo file, CancellationToken cancellationToken)
    {
        var productName = await Product.GetNameFromInformationFile(file, cancellationToken);
        var productUri = Product.GetUri(serviceUri, productName);
        var listApisUri = Api.GetListByProductUri(productUri);

        var publishedApiDisplayNames = await getResources(listApisUri, cancellationToken).Select(jsonObject => jsonObject.GetObjectPropertyValue("properties")
                                                                                                                         .GetNonEmptyStringPropertyValue("displayName"))
                                                                                         .ToListAsync(cancellationToken);

        var fileApiDisplayNames = await file.ReadAsJsonObject(cancellationToken)
                                            .Map(json => json.TryGetObjectArrayPropertyValue("apis")
                                                             .IfNull(() => Enumerable.Empty<JsonObject>())
                                                             .Select(jsonObject => jsonObject.GetNonEmptyStringPropertyValue("displayName")));

        var productApisToDelete = publishedApiDisplayNames.ExceptBy(fileApiDisplayNames, displayName => displayName.Normalize());
        var productApisToCreate = fileApiDisplayNames.ExceptBy(publishedApiDisplayNames, displayName => displayName.Normalize());

        var deletionTasks = productApisToDelete.Select(displayName => GetApiNameFromServiceUri(displayName, cancellationToken).Bind(apiName => DeleteProductApi(productUri, apiName, cancellationToken)));
        var creationTasks = productApisToCreate.Select(displayName => GetApiNameFromServiceDirectory(displayName, cancellationToken).Bind(apiName => PutProductApi(productUri, apiName, cancellationToken)));

        await Task.WhenAll(deletionTasks.Concat(creationTasks));

        return Unit.Default;
    }

    private async Task<Unit> DeleteProductApi(ProductUri productUri, ApiName apiName, CancellationToken cancellationToken)
    {
        var productApiUri = Api.GetUri(productUri, apiName);

        return await deleteResource(productApiUri, cancellationToken);
    }

    private async Task<Unit> PutProductApi(ProductUri productUri, ApiName apiName, CancellationToken cancellationToken)
    {
        var productApiUri = Api.GetUri(productUri, apiName);

        using var payloadStream = new MemoryStream();

        var payloadJson = new JsonObject().AddProperty("properties",
                                                       new JsonObject().AddStringProperty("provisioningState", "created"));

        await payloadJson.SerializeToStream(payloadStream, cancellationToken);

        return await putResource(productApiUri, payloadStream, cancellationToken);
    }

    private async Task<Unit> PutProductPolicy(FileInfo file, CancellationToken cancellationToken)
    {
        Logger.LogInformation($"Updating Azure product policy with file {file}...");

        var productName = await Product.GetNameFromPolicyFile(file, cancellationToken);
        var productUri = Product.GetUri(serviceUri, productName);
        var policyUri = Policy.GetProductPolicyUri(productUri);

        return await PutPolicy(policyUri, file, cancellationToken);
    }

    private async Task<Unit> PutLoggerInformation(FileInfo file, CancellationToken cancellationToken)
    {
        Logger.LogInformation($"Updating Azure logger information with file {file}...");

        var loggerName = await common.Logger.GetNameFromInformationFile(file, cancellationToken);
        var loggerUri = common.Logger.GetUri(serviceUri, loggerName);
        using var fileStream = file.OpenRead();
        return await putResource(loggerUri, fileStream, cancellationToken);
    }

    private async Task<Unit> PutApiInformation(FileInfo file, CancellationToken cancellationToken)
    {
        Logger.LogInformation($"Updating Azure API information with file {file}...");

        var getApiInformationStream = async () =>
        {
            var addSpecificationToJson = async (FileName specificationFileName, string specificationFormat, JsonObject apiJson) =>
            {
                var specificationFile = file.GetDirectoryInfo().GetFileInfo(specificationFileName);

                if (specificationFile.Exists)
                {
                    Logger.LogInformation($"Adding contents of API specification file {specificationFile}...");
                    var specification = await specificationFile.ReadAsText(cancellationToken);

                    var propertiesJson = apiJson.GetObjectPropertyValue("properties")
                                                .AddStringProperty("format", specificationFormat)
                                                .AddStringProperty("value", specification);

                    return apiJson.AddProperty("properties", propertiesJson);
                }
                else
                {
                    return apiJson;
                }
            };

            var addYamlSpecificationToJson = (JsonObject apiJson) => addSpecificationToJson(Constants.ApiYamlSpecificationFileName, "openapi", apiJson);
            var addJsonSpecificationToJson = (JsonObject apiJson) => addSpecificationToJson(Constants.ApiJsonSpecificationFileName, "openapi+json", apiJson);

            var apiJson = await file.ReadAsJsonObject(cancellationToken);
            apiJson = await addJsonSpecificationToJson(apiJson);
            apiJson = await addYamlSpecificationToJson(apiJson);

            var memoryStream = new MemoryStream();
            await apiJson.SerializeToStream(memoryStream, cancellationToken);
            return memoryStream;
        };

        var apiInformationStream = await getApiInformationStream();
        var apiName = await Api.GetNameFromInformationFile(file, cancellationToken);
        var apiUri = Api.GetUri(serviceUri, apiName);

        return await putResource(apiUri, apiInformationStream, cancellationToken);
    }

    private async Task<Unit> PutApiPolicy(FileInfo file, CancellationToken cancellationToken)
    {
        Logger.LogInformation($"Updating Azure API policy with file {file}...");

        var apiName = await Api.GetNameFromPolicyFile(file, cancellationToken);
        var apiUri = Api.GetUri(serviceUri, apiName);
        var policyUri = Policy.GetApiPolicyUri(apiUri);

        return await PutPolicy(policyUri, file, cancellationToken);
    }

    private async Task<Unit> PutApiDiagnosticInformation(FileInfo file, CancellationToken cancellationToken)
    {
        var apiName = await Api.GetNameFromDiagnosticInformationFile(file, cancellationToken);
        var apiUri = Api.GetUri(serviceUri, apiName);
        var diagnosticName = await Diagnostic.GetNameFromInformationFile(file, cancellationToken);
        var diagnosticUri = Diagnostic.GetUri(apiUri, diagnosticName);
        using var fileStream = file.OpenRead();

        return await putResource(diagnosticUri, fileStream, cancellationToken);
    }

    private async Task<Unit> PutOperationPolicy(FileInfo file, CancellationToken cancellationToken)
    {
        Logger.LogInformation($"Updating Azure API operation policy with file {file}...");

        var apiName = await Operation.GetApiNameFromPolicyFile(file, cancellationToken);
        var apiUri = Api.GetUri(serviceUri, apiName);
        var operationName = Operation.GetNameFromPolicyFile(file);
        var operationUri = Operation.GetUri(apiUri, operationName);
        var policyUri = Policy.GetOperationPolicyUri(operationUri);

        return await PutPolicy(policyUri, file, cancellationToken);
    }

    private async Task<Unit> ProcessFilesToDelete(ILookup<FileType, FileInfo> fileLookup, CancellationToken cancellationToken)
    {
        var deleteServiceInformationFile = () => fileLookup.Lookup(FileType.ServiceInformation)
                                                           .FirstOrDefault()
                                                           .Map(file => DeleteServiceInformation(file, cancellationToken))
                                                           .IfNull(() => Task.FromResult(Unit.Default));

        var deleteAuthorizationServers = () => fileLookup.Lookup(FileType.AuthorizationServerInformation)
                                                         .ExecuteInParallel(DeleteAuthorizationServerInformation, cancellationToken);

        var deleteGateways = () => fileLookup.Lookup(FileType.GatewayInformation)
                                             .ExecuteInParallel(DeleteGatewayInformation, cancellationToken);

        var deleteServicePolicy = () => fileLookup.Lookup(FileType.ServicePolicy)
                                                  .FirstOrDefault()
                                                  .Map(file => DeleteServicePolicy(file, cancellationToken))
                                                  .IfNull(() => Task.FromResult(Unit.Default));

        var deleteProducts = () => fileLookup.Lookup(FileType.ProductInformation)
                                             .ExecuteInParallel(DeleteProductInformation, cancellationToken);

        var deleteProductPolicies = () => fileLookup.Lookup(FileType.ProductPolicy)
                                                    .ExecuteInParallel(DeleteProductPolicy, cancellationToken);

        var deleteLoggers = () => fileLookup.Lookup(FileType.LoggerInformation)
                                            .ExecuteInParallel(DeleteLoggerInformation, cancellationToken);

        var deleteServiceDiagnostics = () => fileLookup.Lookup(FileType.ServiceDiagnosticInformation)
                                                       .ExecuteInParallel(DeleteServiceDiagnosticInformation, cancellationToken);

        var deleteApiInformation = () => fileLookup.Lookup(FileType.ApiInformation)
                                                   .ExecuteInParallel(DeleteApiInformation, cancellationToken);

        var deleteApiDiagnostics = () => fileLookup.Lookup(FileType.ApiDiagnosticInformation)
                                                   .ExecuteInParallel(DeleteApiDiagnosticInformation, cancellationToken);

        var deleteApiPolicies = () => fileLookup.Lookup(FileType.ApiPolicy)
                                                .ExecuteInParallel(DeleteApiPolicy, cancellationToken);

        var deleteOperationPolicies = () => fileLookup.Lookup(FileType.OperationPolicy)
                                                      .ExecuteInParallel(DeleteOperationPolicy, cancellationToken);

        await Task.WhenAll(deleteApiPolicies(), deleteApiDiagnostics(), deleteOperationPolicies());

        await deleteApiInformation();

        await Task.WhenAll(deleteProductPolicies(), deleteServiceDiagnostics());

        await Task.WhenAll(deleteAuthorizationServers(),
                           deleteGateways(),
                           deleteServicePolicy(),
                           deleteProducts(),
                           deleteLoggers());

        await deleteServiceInformationFile();

        return Unit.Default;
    }

    private Task<Unit> DeleteServiceInformation(FileInfo file, CancellationToken cancellationToken)
    {
        Logger.LogInformation($"File {file} was deleted; deleting service information...");

        throw new NotImplementedException("Delete service manually. For safety reasons, automatic instance deletion was not implemented.");
    }

    private Task<Unit> DeleteAuthorizationServerInformation(FileInfo file, CancellationToken cancellationToken)
    {
        Logger.LogInformation($"File {file} was deleted; deleting authorization server...");

        return Git.GetPreviousCommitContents(commitId.IfNullThrow("Commit ID cannot be null."), file, serviceDirectory)
                  .Map(fileContents => fileContents.ToJsonObject())
                  .Map(jsonObject => AuthorizationServer.GetNameFromInformationFile(jsonObject))
                  .Map(name => AuthorizationServer.GetUri(serviceUri, name))
                  .Bind(uri => deleteResource(uri, cancellationToken));
    }

    private Task<Unit> DeleteGatewayInformation(FileInfo file, CancellationToken cancellationToken)
    {
        Logger.LogInformation($"File {file} was deleted; deleting gateway...");

        return Git.GetPreviousCommitContents(commitId.IfNullThrow("Commit ID cannot be null."), file, serviceDirectory)
                  .Map(fileContents => fileContents.ToJsonObject())
                  .Map(jsonObject => Gateway.GetNameFromInformationFile(jsonObject))
                  .Map(name => Gateway.GetUri(serviceUri, name))
                  .Bind(uri => deleteResource(uri, cancellationToken));
    }

    private Task<Unit> DeleteServicePolicy(FileInfo file, CancellationToken cancellationToken)
    {
        Logger.LogInformation($"File {file} was deleted; deleting service policy...");

        var policyUri = Policy.GetServicePolicyUri(serviceUri);

        return deleteResource(policyUri, cancellationToken);
    }

    private Task<Unit> DeleteServiceDiagnosticInformation(FileInfo file, CancellationToken cancellationToken)
    {
        Logger.LogInformation($"File {file} was deleted; deleting service diagnostic...");

        return Git.GetPreviousCommitContents(commitId.IfNullThrow("Commit ID cannot be null."), file, serviceDirectory)
                  .Map(fileContents => fileContents.ToJsonObject())
                  .Map(jsonObject => Diagnostic.GetNameFromInformationFile(jsonObject))
                  .Map(name => Diagnostic.GetUri(serviceUri, name))
                  .Bind(uri => deleteResource(uri, cancellationToken));
    }

    private Task<Unit> DeleteProductInformation(FileInfo file, CancellationToken cancellationToken)
    {
        Logger.LogInformation($"File {file} was deleted; deleting service product...");

        return Git.GetPreviousCommitContents(commitId.IfNullThrow("Commit ID cannot be null."), file, serviceDirectory)
                  .Map(fileContents => fileContents.ToJsonObject())
                  .Map(jsonObject => Product.GetNameFromInformationFile(jsonObject))
                  .Map(name => Product.GetUri(serviceUri, name))
                  .Bind(uri => deleteResource(uri, cancellationToken));
    }

    private static string GetDisplayNameFromResourceJson(JsonObject json)
    {
        return json.GetObjectPropertyValue("properties")
                   .GetNonEmptyStringPropertyValue("displayName");
    }

    private async Task<Unit> DeleteProductPolicy(FileInfo productPolicyFile, CancellationToken cancellationToken)
    {
        var productInformationFile = Product.GetInformationFileFromPolicyFile(productPolicyFile);

        if (productInformationFile.Exists)
        {
            Logger.LogInformation($"File {productPolicyFile} was deleted; deleting product policy...");

            var productName = await Product.GetNameFromPolicyFile(productPolicyFile, cancellationToken);
            var productUri = Product.GetUri(serviceUri, productName);
            var policyUri = Policy.GetProductPolicyUri(productUri);

            return await deleteResource(policyUri, cancellationToken);
        }
        else
        {
            Logger.LogInformation($"Product policy file {productPolicyFile} was deleted, but information file {productInformationFile} is missing; skipping product policy deletion...");
            return Unit.Default;
        }
    }

    private Task<Unit> DeleteLoggerInformation(FileInfo file, CancellationToken cancellationToken)
    {
        Logger.LogInformation($"File {file} was deleted; deleting service logger...");

        return Git.GetPreviousCommitContents(commitId.IfNullThrow("Commit ID cannot be null."), file, serviceDirectory)
                  .Map(fileContents => fileContents.ToJsonObject())
                  .Map(jsonObject => common.Logger.GetNameFromInformationFile(jsonObject))
                  .Map(name => common.Logger.GetUri(serviceUri, name))
                  .Bind(uri => deleteResource(uri, cancellationToken));
    }

    private Task<Unit> DeleteApiInformation(FileInfo file, CancellationToken cancellationToken)
    {
        Logger.LogInformation($"File {file} was deleted; deleting API...");

        return Git.GetPreviousCommitContents(commitId.IfNullThrow("Commit ID cannot be null."), file, serviceDirectory)
                  .Map(fileContents => fileContents.ToJsonObject())
                  .Map(jsonObject => Api.GetNameFromInformationFile(jsonObject))
                  .Map(name => Api.GetUri(serviceUri, name))
                  .Bind(uri => deleteResource(uri, cancellationToken));
    }

    private async Task<Unit> DeleteApiPolicy(FileInfo apiPolicyFile, CancellationToken cancellationToken)
    {
        var apiInformationFile = Api.GetInformationFileFromPolicyFile(apiPolicyFile);

        if (apiInformationFile.Exists)
        {
            Logger.LogInformation($"File {apiPolicyFile} was deleted; deleting API policy...");

            var apiName = await Api.GetNameFromPolicyFile(apiPolicyFile, cancellationToken);
            var apiUri = Api.GetUri(serviceUri, apiName);
            var policyUri = Policy.GetApiPolicyUri(apiUri);

            return await deleteResource(policyUri, cancellationToken);
        }
        else
        {
            Logger.LogInformation($"File {apiPolicyFile} was deleted, but information file {apiInformationFile} is missing; skipping API policy deletion...");
            return Unit.Default;
        }
    }

    private async Task<Unit> DeleteApiDiagnosticInformation(FileInfo apiDiagnosticFile, CancellationToken cancellationToken)
    {
        var apiInformationFile = Api.GetInformationFileFromDiagnosticFile(apiDiagnosticFile);

        if (apiInformationFile.Exists)
        {
            Logger.LogInformation($"File {apiDiagnosticFile} was deleted; deleting API diagnostic...");

            var apiName = await Api.GetNameFromDiagnosticInformationFile(apiDiagnosticFile, cancellationToken);
            var apiUri = Api.GetUri(serviceUri, apiName);
            var diagnosticName = DiagnosticName.From(apiDiagnosticFile.GetDirectoryName());
            var diagnosticUri = Diagnostic.GetUri(apiUri, diagnosticName);

            return await deleteResource(diagnosticUri, cancellationToken);
        }
        else
        {
            Logger.LogInformation($"Api diagnostic file {apiDiagnosticFile} was deleted, but information file {apiInformationFile} is missing; skipping API diagnostic deletion...");
            return Unit.Default;
        }
    }

    private async Task<Unit> DeleteOperationPolicy(FileInfo operationPolicyFile, CancellationToken cancellationToken)
    {
        var apiInformationFile = Operation.GetApiInformationFileFromPolicyFile(operationPolicyFile);
        if (apiInformationFile.Exists)
        {
            Logger.LogInformation($"File {operationPolicyFile} was deleted; deleting operation policy...");

            var apiName = await Api.GetNameFromInformationFile(apiInformationFile, cancellationToken);
            var apiUri = Api.GetUri(serviceUri, apiName);
            var operationName = Operation.GetNameFromPolicyFile(operationPolicyFile);
            var operationUri = Operation.GetUri(apiUri, operationName);
            var policyUri = Policy.GetOperationPolicyUri(operationUri);

            return await deleteResource(policyUri, cancellationToken);
        }
        else
        {
            Logger.LogInformation($"File {operationPolicyFile} was deleted, but information file {apiInformationFile} is missing; skipping operation policy deletion...");
            return Unit.Default;
        }
    }
}
