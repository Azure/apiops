namespace common;

public static class ProductInformation
{
    public static JsonObject FormatResponseJson(JsonObject responseJson)
    {
        var propertiesFormatter = (JsonObject source) =>
            new JsonObject().CopyPropertyIfValueIsNonNullFrom("displayName", source)
                            .CopyPropertyIfValueIsNonNullFrom("approvalRequired", source)
                            .CopyPropertyIfValueIsNonNullFrom("description", source)
                            .CopyPropertyIfValueIsNonNullFrom("state", source)
                            .CopyPropertyIfValueIsNonNullFrom("subscriptionRequired", source)
                            .CopyPropertyIfValueIsNonNullFrom("subscriptionsLimit", source)
                            .CopyPropertyIfValueIsNonNullFrom("terms", source);

        return new JsonObject().CopyPropertyFrom("name", responseJson)
                               .CopyObjectPropertyFrom("properties", responseJson, propertiesFormatter);
    }
}
