namespace codegen.resources;

internal sealed record ApiOperation : IResourceWithName, IResourceWithDirectory, IChildResource
{
    public static ApiOperation Instance = new();

    public IResource Parent { get; } = Api.Instance;

    public string CollectionDirectoryType { get; } = "ApiOperationsDirectory";

    public string CollectionDirectoryName { get; } = "operations";

    public string DirectoryType { get; } = "ApiOperationDirectory";

    public string NameType { get; } = "ApiOperationName";

    public string NameParameter { get; } = "apiOperationName";

    public string SingularDescription { get; } = "ApiOperation";

    public string PluralDescription { get; } = "ApiOperations";

    public string LoggerSingularDescription { get; } = "API operation";

    public string LoggerPluralDescription { get; } = "API operations";

    public string CollectionUriType { get; } = "ApiOperationsUri";

    public string CollectionUriPath { get; } = "operations";

    public string UriType { get; } = "ApiOperationUri";
}
