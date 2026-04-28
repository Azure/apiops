namespace common;

public sealed record WorkspaceResource : IResourceWithDirectory
{
    private WorkspaceResource() { }

    public string CollectionDirectoryName { get; } = "workspaces";

    public string SingularName { get; } = "workspace";

    public string PluralName { get; } = "workspaces";

    public string CollectionUriPath { get; } = "workspaces";

    public static WorkspaceResource Instance { get; } = new();
}