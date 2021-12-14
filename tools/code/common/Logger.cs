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

    public static Task<LoggerName> GetNameFromInformationFile(FileInfo loggerInformationFile, CancellationToken cancellationToken)
    {
        return loggerInformationFile.ReadAsJsonObject(cancellationToken)
                                    .Map(json => json.GetNonEmptyStringPropertyValue("name"))
                                    .Map(LoggerName.From);
    }
}
