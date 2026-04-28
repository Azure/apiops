namespace common;

public sealed record WorkspaceTagProductResource : ILinkResource
{
    private WorkspaceTagProductResource() { }

    public string FileName { get; } = "tagProductInformation.json";

    public string DtoPropertyNameForLinkedResource { get; } = "productId";

    public IResourceWithDirectory Primary { get; } = WorkspaceTagResource.Instance;

    public IResourceWithDirectory Secondary { get; } = WorkspaceProductResource.Instance;

    public static WorkspaceTagProductResource Instance { get; } = new();
}