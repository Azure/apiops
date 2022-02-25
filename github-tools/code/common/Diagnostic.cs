namespace common;

public static class Diagnostic
{
    public static Uri GetListByServiceUri(ServiceUri serviceUri)
    {
        return serviceUri.ToUri()
                         .AppendPath("diagnostics");
    }

    public static DiagnosticUri GetUri(ServiceUri serviceUri, DiagnosticName diagnosticName)
    {
        var diagnosticUri = GetListByServiceUri(serviceUri).AppendPath(diagnosticName);

        return DiagnosticUri.From(diagnosticUri);
    }
    
    public static Uri GetListByApiUri(ApiUri apiUri)
    {
        return apiUri.ToUri()
                     .AppendPath("diagnostics");
    }

    public static DiagnosticUri GetUri(ApiUri apiUri, DiagnosticName diagnosticName)
    {
        var diagnosticUri = GetListByApiUri(apiUri).AppendPath(diagnosticName);

        return DiagnosticUri.From(diagnosticUri);
    }

    public static Task<DiagnosticName> GetNameFromInformationFile(FileInfo file, CancellationToken cancellationToken)
    {
        return file.ReadAsJsonObject(cancellationToken)
                   .Map(GetNameFromInformationFile);
    }

    public static DiagnosticName GetNameFromInformationFile(JsonObject fileJson)
    {
        return fileJson.GetNonEmptyStringPropertyValue("name")
                       .Map(DiagnosticName.From)
                       .IfNullThrow("Diagnostic name cannot be null.");
    }
}
