namespace common;

public static class GatewayInformation
{
    public static JsonObject FormatResponseJson(JsonObject responseJson)
    {
        var propertiesFormatter = (JsonObject source) =>
            new JsonObject().CopyPropertyIfValueIsNonNullFrom("description", source)
                            .CopyPropertyIfValueIsNonNullFrom("locationData", source);

        return new JsonObject().CopyPropertyFrom("name", responseJson)
                               .CopyObjectPropertyFrom("properties", responseJson, propertiesFormatter);
    }
}
