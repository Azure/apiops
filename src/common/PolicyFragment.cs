namespace common;

public sealed record PolicyFragmentResource : IPolicyResource, IResourceWithInformationFile
{
    private PolicyFragmentResource() { }

    public static PolicyFragmentResource Instance { get; } = new();

    public string FileName { get; } = "policyFragmentInformation.json";

    public string CollectionDirectoryName { get; } = "policy fragments";

    public string SingularName { get; } = "policy fragment";

    public string PluralName { get; } = "policy fragments";

    public string CollectionUriPath { get; } = "policyFragments";
}