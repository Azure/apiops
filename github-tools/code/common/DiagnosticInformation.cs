namespace common;

public static class DiagnosticInformation
{
    public static JsonObject FormatResponseJson(JsonObject responseJson)
    {
        var bodyDiagnosticSettingsFormatter = (JsonObject source) => new JsonObject().CopyPropertyIfValueIsNonNullFrom("bytes", source);

        var dataMaskingEntityFormatter = (JsonObject source) =>
            new JsonObject().CopyPropertyIfValueIsNonNullFrom("mode", source)
                            .CopyPropertyIfValueIsNonNullFrom("value", source);

        var dataMaskingFormatter = (JsonObject source) =>
            new JsonObject().CopyObjectPropertyIfValueIsNonNullFrom("headers", source, dataMaskingEntityFormatter)
                            .CopyObjectPropertyIfValueIsNonNullFrom("queryParams", source, dataMaskingEntityFormatter);

        var httpMessageDiagnosticFormatter = (JsonObject source) =>
            new JsonObject().CopyObjectPropertyIfValueIsNonNullFrom("body", source, bodyDiagnosticSettingsFormatter)
                            .CopyObjectPropertyIfValueIsNonNullFrom("dataMasking", source, dataMaskingFormatter)
                            .CopyPropertyIfValueIsNonNullFrom("headers", source);

        var pipelineDiagnosticSettingsFormatter = (JsonObject source) =>
            new JsonObject().CopyObjectPropertyIfValueIsNonNullFrom("request", source, httpMessageDiagnosticFormatter)
                            .CopyObjectPropertyIfValueIsNonNullFrom("response", source, httpMessageDiagnosticFormatter);

        var samplingSettingsFormatter = (JsonObject source) =>
            new JsonObject().CopyPropertyIfValueIsNonNullFrom("percentage", source)
                            .CopyPropertyIfValueIsNonNullFrom("samplingType", source);

        var diagnosticContractPropertiesFormatter = (JsonObject source) =>
            new JsonObject().CopyPropertyIfValueIsNonNullFrom("alwaysLog", source)
                            .CopyPropertyIfValueIsNonNullFrom("httpCorrelationProtocol", source)
                            .CopyPropertyIfValueIsNonNullFrom("logClientIp", source)
                            .CopyPropertyIfValueIsNonNullFrom("loggerId", source)
                            .CopyPropertyIfValueIsNonNullFrom("operationNameFormat", source)
                            .CopyPropertyIfValueIsNonNullFrom("verbosity", source)
                            .CopyObjectPropertyIfValueIsNonNullFrom("backend", source, pipelineDiagnosticSettingsFormatter)
                            .CopyObjectPropertyIfValueIsNonNullFrom("frontend", source, pipelineDiagnosticSettingsFormatter)
                            .CopyObjectPropertyIfValueIsNonNullFrom("sampling", source, samplingSettingsFormatter);

        return new JsonObject().CopyPropertyFrom("name", responseJson)
                               .CopyObjectPropertyFrom("properties", responseJson, diagnosticContractPropertiesFormatter);
    }
}
