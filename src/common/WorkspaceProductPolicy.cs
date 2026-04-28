namespace common;

public sealed record WorkspaceProductPolicyResource : IPolicyResource, IChildResource
{
    private WorkspaceProductPolicyResource() { }

    public IResource Parent { get; } = WorkspaceProductResource.Instance;

    public static WorkspaceProductPolicyResource Instance { get; } = new();
}
