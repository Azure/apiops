namespace common;

public sealed record WorkspaceProductGroupResource : ILinkResource
{
    private WorkspaceProductGroupResource() { }

    public string FileName { get; } = "productGroupInformation.json";

    public string DtoPropertyNameForLinkedResource { get; } = "groupId";

    public IResourceWithDirectory Primary { get; } = WorkspaceProductResource.Instance;

    public IResourceWithDirectory Secondary { get; } = WorkspaceGroupResource.Instance;

    public static WorkspaceProductGroupResource Instance { get; } = new();
}
