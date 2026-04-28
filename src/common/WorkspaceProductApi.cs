namespace common;

public sealed record WorkspaceProductApiResource : ILinkResource
{
    private WorkspaceProductApiResource() { }

    public string FileName { get; } = "productApiInformation.json";

    public string DtoPropertyNameForLinkedResource { get; } = "apiId";

    public IResourceWithDirectory Primary { get; } = WorkspaceProductResource.Instance;

    public IResourceWithDirectory Secondary { get; } = WorkspaceApiResource.Instance;

    public static WorkspaceProductApiResource Instance { get; } = new();
}