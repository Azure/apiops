namespace codegen.resources;

internal sealed record ProductApi : ICompositeResource, IResourceWithInformationFile
{
    public static ProductApi Instance = new();

    public IResource First { get; } = Product.Instance;

    public IResource Second { get; } = Api.Instance;

    public string SingularDescription { get; } = "ProductApi";

    public string PluralDescription { get; } = "ProductApis";

    public string LoggerSingularDescription { get; } = "product API";

    public string LoggerPluralDescription { get; } = "product APIs";

    public string CollectionUriType { get; } = "ProductApisUri";

    public string CollectionUriPath { get; } = "apis";

    public string UriType { get; } = "ProductApiUri";

    public string InformationFileType { get; } = "ProductApiInformationFile";

    public string InformationFileName { get; } = "productApiInformation.json";

    public string DtoType { get; } = "ProductApiDto";

    public string DtoCode =>
$$"""
    public static {{DtoType}} Instance { get; } = new();
""";

    public string CollectionDirectoryType { get; } = "ProductApisDirectory";

    public string CollectionDirectoryName => ((IResourceWithDirectory)Second).CollectionDirectoryName;

    public string DirectoryType { get; } = "ProductApiDirectory";
}
