namespace common;

public sealed record TagApiResource : ILinkResource
{
    private TagApiResource() { }

    public string FileName { get; } = "tagApiInformation.json";

    public string DtoPropertyNameForLinkedResource { get; } = "apiId";

    public IResourceWithDirectory Primary { get; } = TagResource.Instance;

    public IResourceWithDirectory Secondary { get; } = ApiResource.Instance;

    public static TagApiResource Instance { get; } = new();
}