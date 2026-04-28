namespace common;

public sealed record ApiOperationPolicyResource : IPolicyResource, IChildResource
{
    private ApiOperationPolicyResource() { }

    public IResource Parent { get; } = ApiOperationResource.Instance;

    public static ApiOperationPolicyResource Instance { get; } = new();
}