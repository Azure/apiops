namespace common;

public sealed record ProductPolicyResource : IPolicyResource, IChildResource
{
    private ProductPolicyResource() { }

    public IResource Parent { get; } = ProductResource.Instance;

    public static ProductPolicyResource Instance { get; } = new();
}