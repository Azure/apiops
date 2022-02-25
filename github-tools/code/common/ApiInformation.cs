namespace common;

public static class ApiInformation
{
    public static JsonObject FormatResponseJson(JsonObject responseJson)
    {
        var apiVersionSetContractDetailsFormatter = (JsonObject source) =>
            new JsonObject().CopyPropertyIfValueIsNonNullFrom("description", source)
                            .CopyPropertyIfValueIsNonNullFrom("id", source)
                            .CopyPropertyIfValueIsNonNullFrom("name", source)
                            .CopyPropertyIfValueIsNonNullFrom("versionHeaderName", source)
                            .CopyPropertyIfValueIsNonNullFrom("versioningScheme", source)
                            .CopyPropertyIfValueIsNonNullFrom("versionQueryName", source);

        var oAuth2AuthenticationSettingsContractFormatter = (JsonObject source) =>
            new JsonObject().CopyPropertyIfValueIsNonNullFrom("authorizationServerId", source)
                            .CopyPropertyIfValueIsNonNullFrom("scope", source);

        var openIdAuthenticationSettingsContractFormatter = (JsonObject source) =>
            new JsonObject().CopyPropertyIfValueIsNonNullFrom("bearerTokenSendingMethods", source)
                            .CopyPropertyIfValueIsNonNullFrom("openidProviderId", source);

        var authenticationSettingsContractFormatter = (JsonObject source) =>
            new JsonObject().CopyObjectPropertyIfValueIsNonNullFrom("oAuth2", source, oAuth2AuthenticationSettingsContractFormatter)
                            .CopyObjectPropertyIfValueIsNonNullFrom("openid", source, openIdAuthenticationSettingsContractFormatter);

        var apiContactInformationFormatter = (JsonObject source) =>
            new JsonObject().CopyPropertyIfValueIsNonNullFrom("email", source)
                            .CopyPropertyIfValueIsNonNullFrom("name", source)
                            .CopyPropertyIfValueIsNonNullFrom("url", source);

        var apiLicenseInformationFormatter = (JsonObject source) =>
            new JsonObject().CopyPropertyIfValueIsNonNullFrom("name", source)
                            .CopyPropertyIfValueIsNonNullFrom("url", source);

        var subscriptionKeyParameterNamesContractFormatter = (JsonObject source) =>
            new JsonObject().CopyPropertyIfValueIsNonNullFrom("header", source)
                            .CopyPropertyIfValueIsNonNullFrom("query", source);

        var propertiesFormatter = (JsonObject source) =>
            new JsonObject().CopyPropertyIfValueIsNonNullFrom("apiRevision", source)
                            .CopyPropertyIfValueIsNonNullFrom("apiRevisionDescription", source)
                            .CopyPropertyIfValueIsNonNullFrom("apiType", source)
                            .CopyPropertyIfValueIsNonNullFrom("apiVersion", source)
                            .CopyPropertyIfValueIsNonNullFrom("apiVersionDescription", source)
                            .CopyObjectPropertyIfValueIsNonNullFrom("apiVersionSet", source, apiVersionSetContractDetailsFormatter)
                            .CopyPropertyIfValueIsNonNullFrom("apiVersionSetId", source)
                            .CopyObjectPropertyIfValueIsNonNullFrom("authenticationSettings", source, authenticationSettingsContractFormatter)
                            .CopyObjectPropertyIfValueIsNonNullFrom("contact", source, apiContactInformationFormatter)
                            .CopyPropertyIfValueIsNonNullFrom("description", source)
                            .CopyPropertyIfValueIsNonNullFrom("displayName", source)
                            .CopyPropertyIfValueIsNonNullFrom("format", source)
                            .CopyPropertyIfValueIsNonNullFrom("isCurrent", source)
                            .CopyObjectPropertyIfValueIsNonNullFrom("license", source, apiLicenseInformationFormatter)
                            .CopyPropertyIfValueIsNonNullFrom("path", source)
                            .CopyPropertyIfValueIsNonNullFrom("protocols", source)
                            .CopyPropertyIfValueIsNonNullFrom("serviceUrl", source)
                            .CopyPropertyIfValueIsNonNullFrom("sourceApiId", source)
                            .CopyObjectPropertyIfValueIsNonNullFrom("subscriptionKeyParameterNames", source, subscriptionKeyParameterNamesContractFormatter)
                            .CopyPropertyIfValueIsNonNullFrom("subscriptionRequired", source)
                            .CopyPropertyIfValueIsNonNullFrom("termsOfServiceUrl", source)
                            .CopyPropertyIfValueIsNonNullFrom("type", source);

        return new JsonObject().CopyPropertyFrom("name", responseJson)
                               .CopyObjectPropertyFrom("properties", responseJson, propertiesFormatter);
    }
}
