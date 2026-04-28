namespace common;

public sealed record ApiOperationResource : IResourceWithDirectory, IChildResource
{
    private ApiOperationResource() { }

    public string CollectionDirectoryName { get; } = "operations";

    public string SingularName { get; } = "operation";

    public string PluralName { get; } = "operations";

    public string CollectionUriPath { get; } = "operations";

    public IResource Parent { get; } = ApiResource.Instance;

    public static ApiOperationResource Instance { get; } = new();
}