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

    public static Task<GatewayName> GetNameFromInformationFile(FileInfo gatewayInformationFile, CancellationToken cancellationToken)
    {
        return gatewayInformationFile.ReadAsJsonObject(cancellationToken)
                                     .Map(json => json.GetNonEmptyStringPropertyValue("name"))
                                     .Map(GatewayName.From);
    }
}
