namespace common;

public static class Constants
{
    public static FileName ServiceInformationFileName => FileName.From("serviceInformation.json");
    public static FileName PolicyFileName => FileName.From("policy.xml");
    public static DirectoryName ProductsFolderName => DirectoryName.From("products");
    public static FileName ProductInformationFileName => FileName.From("productInformation.json");
    public static DirectoryName GatewaysFolderName => DirectoryName.From("gateways");
    public static FileName GatewayInformationFileName => FileName.From("gatewayInformation.json");
    public static DirectoryName AuthorizationServersFolderName => DirectoryName.From("authorizationServers");
    public static FileName AuthorizationServerInformationFileName => FileName.From("authorizationServerInformation.json");
    public static DirectoryName DiagnosticsFolderName => DirectoryName.From("diagnostics");
    public static FileName DiagnosticInformationFileName => FileName.From("diagnosticInformation.json");
    public static DirectoryName LoggersFolderName => DirectoryName.From("loggers");
    public static FileName LoggerInformationFileName => FileName.From("loggerInformation.json");
    public static DirectoryName ApisFolderName => DirectoryName.From("apis");
    public static FileName ApiInformationFileName => FileName.From("apiInformation.json");
    public static FileName ApiYamlSpecificationFileName => FileName.From("specification.yaml");
    public static FileName ApiJsonSpecificationFileName => FileName.From("specification.json");
    public static DirectoryName OperationsFolderName => DirectoryName.From("operations");
}