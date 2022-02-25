namespace common;

public static class LoggerInformation
{
    public static JsonObject FormatResponseJson(JsonObject responseJson)
    {
        var loggerContractPropertiesFormatter = (JsonObject source) =>
            new JsonObject().CopyPropertyIfValueIsNonNullFrom("credentials", source)
                            .CopyPropertyIfValueIsNonNullFrom("description", source)
                            .CopyPropertyIfValueIsNonNullFrom("isBuffered", source)
                            .CopyPropertyIfValueIsNonNullFrom("loggerType", source)
                            .CopyPropertyIfValueIsNonNullFrom("resourceId", source);

        return new JsonObject().CopyPropertyFrom("name", responseJson)
                               .CopyObjectPropertyFrom("properties", responseJson, loggerContractPropertiesFormatter);
    }
}