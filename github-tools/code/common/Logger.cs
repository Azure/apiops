namespace common;

public static class Logger
{
    public static Uri GetListByServiceUri(ServiceUri serviceUri)
    {
        return serviceUri.ToUri()
                         .AppendPath("loggers");
    }

    public static LoggerUri GetUri(ServiceUri serviceUri, LoggerName loggerName)
    {
        var loggerUri = GetListByServiceUri(serviceUri).AppendPath(loggerName);

        return LoggerUri.From(loggerUri);
    }

    public static Task<LoggerName> GetNameFromInformationFile(FileInfo file, CancellationToken cancellationToken)
    {
        return file.ReadAsJsonObject(cancellationToken)
                   .Map(GetNameFromInformationFile);
    }

    public static LoggerName GetNameFromInformationFile(JsonObject fileJson)
    {
        return fileJson.GetNonEmptyStringPropertyValue("name")
                       .Map(LoggerName.From)
                       .IfNullThrow("Logger name cannot be null.");
    }
}
