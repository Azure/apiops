namespace common;

public static class Service
{
    public static Task<ServiceName> GetNameFromInformationFile(FileInfo serviceInformationFile, CancellationToken cancellationToken)
    {
        return serviceInformationFile.ReadAsJsonObject(cancellationToken)
                                 .Map(json => json.GetNonEmptyStringPropertyValue("name"))
                                 .Map(ServiceName.From);
    }
}
