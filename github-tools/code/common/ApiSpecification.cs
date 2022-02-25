namespace common;

public static class ApiSpecification
{
    public static Uri GetExportUri(ApiUri apiUri, ApiSpecificationFormat specificationFormat)
    {
        var formatParameter = specificationFormat switch
        {
            ApiSpecificationFormat.Json => "openapi+json-link",
            _=> "openapi-link"
        };

        return apiUri.ToUri()
                     .SetQueryParameter("export", "true")
                     .SetQueryParameter("format", formatParameter);
    }

    public static Uri GetDownloadUriFromResponse(JsonObject responseJson)
    {
        var url = responseJson.GetObjectPropertyValue("value")
                              .GetNonEmptyStringPropertyValue("link");

        return new Uri(url);
    }
}
