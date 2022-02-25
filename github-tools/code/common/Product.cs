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

    public static Task<ProductName> GetNameFromInformationFile(FileInfo file, CancellationToken cancellationToken)
    {
        return file.ReadAsJsonObject(cancellationToken)
                   .Map(GetNameFromInformationFile);
    }

    public static ProductName GetNameFromInformationFile(JsonObject fileJson)
    {
        return fileJson.GetNonEmptyStringPropertyValue("name")
                       .Map(ProductName.From)
                       .IfNullThrow("Product name cannot be null.");
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
