namespace common;

public static class Policy
{
    public static Uri GetListByServiceUri(ServiceUri serviceUri)
    {
        return serviceUri.ToUri()
                         .AppendPath("policies")
                         .SetQueryParameter("format", "rawxml");
    }

    public static Uri GetServicePolicyUri(ServiceUri serviceUri)
    {
        return serviceUri.ToUri()
                         .AppendPath("policies")
                         .AppendPath("policy");
    }

    public static Uri GetListByProductUri(ProductUri productUri)
    {
        return productUri.ToUri()
                         .AppendPath("policies")
                         .SetQueryParameter("format", "rawxml");
    }

    public static Uri GetProductPolicyUri(ProductUri productUri)
    {
        return productUri.ToUri()
                         .AppendPath("policies")
                         .AppendPath("policy");
    }

    public static Uri GetListByApiUri(ApiUri apiUri)
    {
        return apiUri.ToUri()
                     .AppendPath("policies")
                     .SetQueryParameter("format", "rawxml");
    }

    public static Uri GetApiPolicyUri(ApiUri apiUri)
    {
        return apiUri.ToUri()
                     .AppendPath("policies")
                     .AppendPath("policy");
    }

    public static Uri GetListByOperationUri(OperationUri operationUri)
    {
        return operationUri.ToUri()
                           .AppendPath("policies")
                           .SetQueryParameter("format", "rawxml");
    }

    public static Uri GetOperationPolicyUri(OperationUri operationUri)
    {
        return operationUri.ToUri()
                           .AppendPath("policies")
                           .AppendPath("policy");
    }

    public static string? TryGetFromResponseJson(JsonObject responseJson)
    {
        return responseJson.TryGetObjectPropertyValue("properties")
                           .Bind(propertiesJson => propertiesJson.TryGetNonEmptyStringPropertyValue("value"));
    }
}
