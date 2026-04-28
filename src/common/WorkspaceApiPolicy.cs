namespace common;

public sealed record WorkspaceApiPolicyResource : IPolicyResource, IChildResource
{
    private WorkspaceApiPolicyResource() { }

    public IResource Parent { get; } = WorkspaceApiResource.Instance;

    public static WorkspaceApiPolicyResource Instance { get; } = new();
}