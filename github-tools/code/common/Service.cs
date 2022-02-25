namespace common;

public static class Service
{
    public static Task<ServiceName> GetNameFromInformationFile(FileInfo file, CancellationToken cancellationToken)
    {
        return file.ReadAsJsonObject(cancellationToken)
                   .Map(GetNameFromInformationFile);
    }

    public static ServiceName GetNameFromInformationFile(JsonObject fileJson)
    {
        return fileJson.GetNonEmptyStringPropertyValue("name")
                       .Map(ServiceName.From)
                       .IfNullThrow("Service name cannot be null.");
    }
}
