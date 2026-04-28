namespace common;

public sealed record WorkspaceApiOperationResource : IResourceWithDirectory, IChildResource
{
    private WorkspaceApiOperationResource() { }

    public string CollectionDirectoryName { get; } = "operations";

    public string SingularName { get; } = "operation";

    public string PluralName { get; } = "operations";

    public string CollectionUriPath { get; } = "operations";

    public IResource Parent { get; } = WorkspaceApiResource.Instance;

    public static WorkspaceApiOperationResource Instance { get; } = new();
}