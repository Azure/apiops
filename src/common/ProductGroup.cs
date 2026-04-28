namespace common;

public sealed record ProductGroupResource : ILinkResource
{
    private ProductGroupResource() { }

    public string FileName { get; } = "productGroupInformation.json";

    public string DtoPropertyNameForLinkedResource { get; } = "groupId";

    public IResourceWithDirectory Primary { get; } = ProductResource.Instance;

    public IResourceWithDirectory Secondary { get; } = GroupResource.Instance;

    public static ProductGroupResource Instance { get; } = new();
}