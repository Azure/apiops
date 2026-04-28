namespace common;

public sealed record WorkspaceApiOperationPolicyResource : IPolicyResource, IChildResource
{
    private WorkspaceApiOperationPolicyResource() { }

    public IResource Parent { get; } = WorkspaceApiOperationResource.Instance;

    public static WorkspaceApiOperationPolicyResource Instance { get; } = new();
}