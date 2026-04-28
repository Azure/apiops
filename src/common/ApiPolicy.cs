namespace common;

public sealed record ApiPolicyResource : IPolicyResource, IChildResource
{
    private ApiPolicyResource() { }

    public IResource Parent { get; } = ApiResource.Instance;

    public static ApiPolicyResource Instance { get; } = new();
}