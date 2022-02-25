namespace common;

public static class Api
{
    public static Uri GetListByServiceUri(ServiceUri serviceUri)
    {
        return serviceUri.ToUri()
                         .AppendPath("apis");
    }

    public static Uri GetListByProductUri(ProductUri productUri)
    {
        return productUri.ToUri()
                         .AppendPath("apis");
    }

    public static Uri GetListByGatewayUri(GatewayUri gatewayUri)
    {
        return gatewayUri.ToUri()
                         .AppendPath("apis");
    }

    public static ApiUri GetUri(ServiceUri serviceUri, ApiName apiName)
    {
        var apiUri = GetListByServiceUri(serviceUri).AppendPath(apiName);

        return ApiUri.From(apiUri);
    }

    public static Uri GetUri(ProductUri productUri, ApiName apiName)
    {
        return GetListByProductUri(productUri).AppendPath(apiName);
    }

    public static Uri GetUri(GatewayUri gatewayUri, ApiName apiName)
    {
        return GetListByGatewayUri(gatewayUri).AppendPath(apiName);
    }

    public static Task<ApiName> GetNameFromInformationFile(FileInfo apiInformationFile, CancellationToken cancellationToken)
    {
        return apiInformationFile.ReadAsJsonObject(cancellationToken)
                                 .Map(GetNameFromInformationFile);
    }

    public static ApiName GetNameFromInformationFile(JsonObject apiInformationFileJson)
    {
        return apiInformationFileJson.GetNonEmptyStringPropertyValue("name")
                                     .Map(ApiName.From)
                                     .IfNullThrow("API name cannot be null.");
    }

    public static Task<ApiName> GetNameFromPolicyFile(FileInfo apiPolicyFile, CancellationToken cancellationToken)
    {
        var apiInformationFile = GetInformationFileFromPolicyFile(apiPolicyFile);

        return GetNameFromInformationFile(apiInformationFile, cancellationToken);
    }

    public static Task<ApiName> GetNameFromDiagnosticInformationFile(FileInfo apiDiagnosticInformationFile, CancellationToken cancellationToken)
    {
        var apiInformationFile = GetInformationFileFromDiagnosticFile(apiDiagnosticInformationFile);

        return GetNameFromInformationFile(apiInformationFile, cancellationToken);
    }

    public static FileInfo GetInformationFileFromPolicyFile(FileInfo apiPolicyFile)
    {
        return apiPolicyFile.GetDirectoryInfo()
                            .GetFileInfo(Constants.ApiInformationFileName);
    }

    public static FileInfo GetInformationFileFromDiagnosticFile(FileInfo apiDiagnosticInformationFile)
    {
        return apiDiagnosticInformationFile.GetDirectoryInfo()
                                           .GetParentDirectory()
                                           .GetParentDirectory()
                                           .GetFileInfo(Constants.ApiInformationFileName);
    }
}