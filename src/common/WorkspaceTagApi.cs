namespace common;

public sealed record WorkspaceTagApiResource : ILinkResource
{
    private WorkspaceTagApiResource() { }

    public string FileName { get; } = "tagApiInformation.json";

    public string DtoPropertyNameForLinkedResource { get; } = "apiId";

    public IResourceWithDirectory Primary { get; } = WorkspaceTagResource.Instance;

    public IResourceWithDirectory Secondary { get; } = WorkspaceApiResource.Instance;

    public static WorkspaceTagApiResource Instance { get; } = new();
}