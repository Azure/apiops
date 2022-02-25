namespace common;

public static class AuthorizationServerInformation
{
    public static JsonObject FormatResponseJson(JsonObject responseJson)
    {
        var tokenBodyParameterContractFormatter = (JsonObject source) =>
                       new JsonObject().CopyPropertyIfValueIsNonNullFrom("name", source)
                                       .CopyPropertyIfValueIsNonNullFrom("value", source);

        var authorizationServerContractPropertiesFormatter = (JsonObject source) =>
            new JsonObject().CopyPropertyIfValueIsNonNullFrom("authorizationEndpoint", source)
                            .CopyPropertyIfValueIsNonNullFrom("authorizationMethods", source)
                            .CopyPropertyIfValueIsNonNullFrom("bearerTokenSendingMethods", source)
                            .CopyPropertyIfValueIsNonNullFrom("clientAuthenticationMethod", source)
                            .CopyPropertyIfValueIsNonNullFrom("clientId", source)
                            .CopyPropertyIfValueIsNonNullFrom("clientRegistrationEndpoint", source)
                            .CopyPropertyIfValueIsNonNullFrom("clientSecret", source)
                            .CopyPropertyIfValueIsNonNullFrom("defaultScope", source)
                            .CopyPropertyIfValueIsNonNullFrom("description", source)
                            .CopyPropertyIfValueIsNonNullFrom("displayName", source)
                            .CopyPropertyIfValueIsNonNullFrom("grantTypes", source)
                            .CopyPropertyIfValueIsNonNullFrom("resourceOwnerPassword", source)
                            .CopyPropertyIfValueIsNonNullFrom("resourceOwnerUsername", source)
                            .CopyPropertyIfValueIsNonNullFrom("supportState", source)
                            .CopyObjectPropertyIfValueIsNonNullFrom("tokenBodyParameters", source, tokenBodyParameterContractFormatter)
                            .CopyPropertyIfValueIsNonNullFrom("tokenEndpoint", source);

        return new JsonObject().CopyPropertyFrom("name", responseJson)
                               .CopyObjectPropertyFrom("properties", responseJson, authorizationServerContractPropertiesFormatter);
    }
}
