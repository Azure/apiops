namespace common;

public static class Gateway
{
    public static Uri GetListByServiceUri(ServiceUri serviceUri)
    {
        return serviceUri.ToUri()
                         .AppendPath("gateways");
    }

    public static GatewayUri GetUri(ServiceUri serviceUri, GatewayName gatewayName)
    {
        var gatewayUri = GetListByServiceUri(serviceUri).AppendPath(gatewayName);

        return GatewayUri.From(gatewayUri);
    }

    public static Task<GatewayName> GetNameFromInformationFile(FileInfo file, CancellationToken cancellationToken)
    {
        return file.ReadAsJsonObject(cancellationToken)
                   .Map(GetNameFromInformationFile);
    }

    public static GatewayName GetNameFromInformationFile(JsonObject fileJson)
    {
        return fileJson.GetNonEmptyStringPropertyValue("name")
                       .Map(GatewayName.From)
                       .IfNullThrow("Gateway name cannot be null.");
    }
}
