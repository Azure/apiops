namespace common;

public sealed record ProductApiResource : ILinkResource
{
    private ProductApiResource() { }

    public string FileName { get; } = "productApiInformation.json";

    public string DtoPropertyNameForLinkedResource { get; } = "apiId";

    public IResourceWithDirectory Primary { get; } = ProductResource.Instance;

    public IResourceWithDirectory Secondary { get; } = ApiResource.Instance;

    public static ProductApiResource Instance { get; } = new();
}