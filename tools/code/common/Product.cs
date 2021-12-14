namespace common;

public static class Product
{
    public static Uri GetListByServiceUri(ServiceUri serviceUri)
    {
        return serviceUri.ToUri()
                         .AppendPath("products");
    }

    public static ProductUri GetUri(ServiceUri serviceUri, ProductName productName)
    {
        var productUri = GetListByServiceUri(serviceUri).AppendPath(productName);

        return ProductUri.From(productUri);
    }

    public static Task<ProductName> GetNameFromInformationFile(FileInfo productInformationFile, CancellationToken cancellationToken)
    {
        return productInformationFile.ReadAsJsonObject(cancellationToken)
                                     .Map(json => json.GetNonEmptyStringPropertyValue("name"))
                                     .Map(ProductName.From);
    }

    public static Task<ProductName> GetNameFromPolicyFile(FileInfo productPolicyFile, CancellationToken cancellationToken)
    {
        var productInformationFile = GetInformationFileFromPolicyFile(productPolicyFile);

        return GetNameFromInformationFile(productInformationFile, cancellationToken);
    }

    public static FileInfo GetInformationFileFromPolicyFile(FileInfo productPolicyFile)
    {
        return productPolicyFile.GetDirectoryInfo()
                                .GetFileInfo(Constants.ProductInformationFileName);
    }
}
