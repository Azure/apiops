namespace common;

public static class Operation
{
    public static Uri GetListByApiUri(ApiUri apiUri)
    {
        return apiUri.ToUri()
                     .AppendPath("operations");
    }

    public static OperationUri GetUri(ApiUri apiUri, OperationName operationName)
    {
        var operationUri = GetListByApiUri(apiUri).AppendPath(operationName);

        return OperationUri.From(operationUri);
    }

    public static OperationName GetNameFromPolicyFile(FileInfo operationPolicyFile)
    {
        var specificationFile = GetApiSpecificationFileFromPolicyFile(operationPolicyFile);
        using var specificationFileStream = specificationFile.OpenRead();
        var specificationDocument = new OpenApiStreamReader().Read(specificationFileStream, out var _);
        var operationDisplayName = operationPolicyFile.GetDirectoryInfo().Name;

        return specificationDocument.Paths.Values.SelectMany(pathItem => pathItem.Operations.Values)
                                                 .FirstOrDefault(operation => operation.Summary.Equals(operationDisplayName, StringComparison.OrdinalIgnoreCase))
                                                 .Map(operation => OperationName.From(operation.OperationId))
                                                 .IfNullThrow($"Could not find operation with display name {operationDisplayName} in specification file {specificationFile}.");
    }

    public static Task<ApiName> GetApiNameFromPolicyFile(FileInfo operationPolicyFile, CancellationToken cancellationToken)
    {
        var apiInformationFile = GetApiInformationFileFromPolicyFile(operationPolicyFile);

        return Api.GetNameFromInformationFile(apiInformationFile, cancellationToken);
    }

    public static FileInfo GetApiInformationFileFromPolicyFile(FileInfo operationPolicyFile)
    {
        var apiDirectory = GetApiDirectoryFromPolicyFile(operationPolicyFile);

        return apiDirectory.GetFileInfo(Constants.ApiInformationFileName);
    }

    public static FileInfo GetApiSpecificationFileFromPolicyFile(FileInfo operationPolicyFile)
    {
        var apiDirectory = GetApiDirectoryFromPolicyFile(operationPolicyFile);

        return apiDirectory.GetFiles()
                           .FirstOrDefault(file =>
                           {
                               var fileName = FileName.From(file.Name);
                               return fileName == Constants.ApiYamlSpecificationFileName || fileName == Constants.ApiJsonSpecificationFileName;
                           })
                           .IfNullThrow($"Could not find API specification file corresponding to operation policy file {operationPolicyFile}.");
    }

    private static DirectoryInfo GetApiDirectoryFromPolicyFile(FileInfo operationPolicyFile)
    {
        return operationPolicyFile.GetDirectoryInfo()
                                  .GetParentDirectory()
                                  .GetParentDirectory();
    }
}