namespace common;

public sealed record TagProductResource : ILinkResource
{
    private TagProductResource() { }

    public string FileName { get; } = "tagProductInformation.json";

    public string DtoPropertyNameForLinkedResource { get; } = "productId";

    public IResourceWithDirectory Primary { get; } = TagResource.Instance;

    public IResourceWithDirectory Secondary { get; } = ProductResource.Instance;

    public static TagProductResource Instance { get; } = new();
}