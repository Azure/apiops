using System.Text.Json.Nodes;

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

public static partial class ResourceModule
{
    /// <summary>
    /// Remove `properties.format` and `properties.value` from the policy fragment information file DTO.
    /// Policy contents will be stored separately.
    /// </summary>
    private static JsonObject FormatInformationFileDto(this PolicyFragmentResource resource, JsonObject dto) =>
       dto.GetJsonObjectProperty("properties")
          .Map(properties => dto.SetProperty("properties", properties.RemoveProperty("format")
                                                                     .RemoveProperty("value")))
          .IfError(_ => dto);
}