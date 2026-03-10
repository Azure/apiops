namespace common;

public sealed record ProductApiResource : ICompositeResource
{
    private ProductApiResource() { }

    public string FileName { get; } = "productApiInformation.json";

    public IResourceWithDirectory Primary { get; } = ProductResource.Instance;

    public IResourceWithDirectory Secondary { get; } = ApiResource.Instance;

    public static ProductApiResource Instance { get; } = new();
}