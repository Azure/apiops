namespace common;

public static class AuthorizationServer
{
    public static Uri GetListByServiceUri(ServiceUri serviceUri)
    {
        return serviceUri.ToUri()
                         .AppendPath("authorizationServers");
    }

    public static AuthorizationServerUri GetUri(ServiceUri serviceUri, AuthorizationServerName authorizationServerName)
    {
        var authorizationServerUri = GetListByServiceUri(serviceUri).AppendPath(authorizationServerName);

        return AuthorizationServerUri.From(authorizationServerUri);
    }

    public static Task<AuthorizationServerName> GetNameFromInformationFile(FileInfo file, CancellationToken cancellationToken)
    {
        return file.ReadAsJsonObject(cancellationToken)
                   .Map(GetNameFromInformationFile);
    }

    public static AuthorizationServerName GetNameFromInformationFile(JsonObject fileJson)
    {
        return fileJson.GetNonEmptyStringPropertyValue("name")
                       .Map(AuthorizationServerName.From)
                       .IfNullThrow("Authorization server name cannot be null.");
    }
}
