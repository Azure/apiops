namespace creator;

public enum ResourceAction
{
    Put,
    Delete
}

public record FileType
{
    private readonly string fileType;

    private FileType(string fileType)
    {
        this.fileType = fileType;
    }

    public string GetFileTypeString() => ToString();
    public override string ToString() => fileType;

    public static FileType ServiceInformation { get; } = new(nameof(ServiceInformation));
    public static FileType ServicePolicy { get; } = new(nameof(ServicePolicy));
    public static FileType ServiceDiagnosticInformation { get; } = new(nameof(ServiceDiagnosticInformation));
    public static FileType ProductInformation { get; } = new(nameof(ProductInformation));
    public static FileType ProductPolicy { get; } = new(nameof(ProductPolicy));
    public static FileType LoggerInformation { get; } = new(nameof(LoggerInformation));
    public static FileType GatewayInformation { get; } = new(nameof(GatewayInformation));
    public static FileType AuthorizationServerInformation { get; } = new(nameof(AuthorizationServerInformation));
    public static FileType ApiInformation { get; } = new(nameof(ApiInformation));
    public static FileType ApiYamlSpecification { get; } = new(nameof(ApiYamlSpecification));
    public static FileType ApiJsonSpecification { get; } = new(nameof(ApiJsonSpecification));
    public static FileType ApiPolicy { get; } = new(nameof(ApiPolicy));
    public static FileType ApiDiagnosticInformation { get; } = new(nameof(ApiDiagnosticInformation));
    public static FileType OperationPolicy { get; } = new(nameof(OperationPolicy));

    public static FileType? TryGetFileType(DirectoryInfo serviceDirectory, FileInfo file)
    {
        var fileName = FileName.From(file.Name);

        return fileName switch
        {
            _ when fileName == Constants.ServiceInformationFileName
                   && file.Directory.PathEquals(serviceDirectory) => FileType.ServiceInformation,
            _ when fileName == Constants.PolicyFileName
                   && file.Directory.PathEquals(serviceDirectory) => FileType.ServicePolicy,
            _ when fileName == Constants.DiagnosticInformationFileName
                   && file.Directory?.Parent?.GetDirectoryName() == Constants.DiagnosticsFolderName
                   && file.Directory?.Parent?.Parent.PathEquals(serviceDirectory) is true => FileType.ServiceDiagnosticInformation,
            _ when fileName == Constants.ProductInformationFileName
                   && file.Directory?.Parent?.GetDirectoryName() == Constants.ProductsFolderName
                   && file.Directory?.Parent?.Parent.PathEquals(serviceDirectory) is true => FileType.ProductInformation,
            _ when fileName == Constants.PolicyFileName
                   && file.Directory?.Parent?.GetDirectoryName() == Constants.ProductsFolderName
                   && file.Directory?.Parent?.Parent.PathEquals(serviceDirectory) is true => FileType.ProductPolicy,
            _ when fileName == Constants.LoggerInformationFileName
                   && file.Directory?.Parent?.GetDirectoryName() == Constants.LoggersFolderName
                   && file.Directory?.Parent?.Parent.PathEquals(serviceDirectory) is true => FileType.LoggerInformation,
            _ when fileName == Constants.GatewayInformationFileName
                   && file.Directory?.Parent?.GetDirectoryName() == Constants.GatewaysFolderName
                   && file.Directory?.Parent?.Parent.PathEquals(serviceDirectory) is true => FileType.GatewayInformation,
            _ when fileName == Constants.AuthorizationServerInformationFileName
                   && file.Directory?.Parent?.GetDirectoryName() == Constants.AuthorizationServersFolderName
                   && file.Directory?.Parent?.Parent.PathEquals(serviceDirectory) is true => FileType.AuthorizationServerInformation,
            _ when fileName == Constants.ApiInformationFileName
                   && file.Directory?.Parent?.GetDirectoryName() == Constants.ApisFolderName
                   && file.Directory?.Parent?.Parent.PathEquals(serviceDirectory) is true => FileType.ApiInformation,
            _ when fileName == Constants.ApiYamlSpecificationFileName
                   && file.Directory?.Parent?.GetDirectoryName() == Constants.ApisFolderName
                   && file.Directory?.Parent?.Parent.PathEquals(serviceDirectory) is true => FileType.ApiYamlSpecification,
            _ when fileName == Constants.ApiJsonSpecificationFileName
                   && file.Directory?.Parent?.GetDirectoryName() == Constants.ApisFolderName
                   && file.Directory?.Parent?.Parent.PathEquals(serviceDirectory) is true => FileType.ApiJsonSpecification,
            _ when fileName == Constants.PolicyFileName
                   && file.Directory?.Parent?.GetDirectoryName() == Constants.ApisFolderName
                   && file.Directory?.Parent?.Parent.PathEquals(serviceDirectory) is true => FileType.ApiPolicy,
            _ when fileName == Constants.DiagnosticInformationFileName
                   && file.Directory?.Parent?.GetDirectoryName() == Constants.DiagnosticsFolderName
                   && file.Directory?.Parent?.Parent?.Parent.GetDirectoryName() == Constants.ApisFolderName
                   && file.Directory?.Parent?.Parent?.Parent?.Parent.PathEquals(serviceDirectory) is true => FileType.ApiDiagnosticInformation,
            _ when fileName == Constants.PolicyFileName
                   && file.Directory?.Parent?.GetDirectoryName() == Constants.OperationsFolderName
                   && file.Directory?.Parent?.Parent?.Parent.GetDirectoryName() == Constants.ApisFolderName
                   && file.Directory?.Parent?.Parent?.Parent?.Parent.PathEquals(serviceDirectory) is true => FileType.OperationPolicy,
            _ => null
        };
    }
}