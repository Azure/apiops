using System;
using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace common;

public sealed record ApiReleaseResource : IResourceWithReference, IChildResource
{
    private ApiReleaseResource() { }

    public string FileName { get; } = "apiReleaseInformation.json";

    public string CollectionDirectoryName { get; } = "releases";

    public string SingularName { get; } = "release";

    public string PluralName { get; } = "releases";

    public string CollectionUriPath { get; } = "releases";

    public Type DtoType { get; } = typeof(ApiReleaseDto);

    public IResource Parent { get; } = ApiResource.Instance;

    public ImmutableDictionary<IResource, string> MandatoryReferencedResourceDtoProperties { get; } =
        ImmutableDictionary.Create<IResource, string>()
                           .Add(ApiResource.Instance, nameof(ApiReleaseDto.Properties.ApiId));

    public static ApiReleaseResource Instance { get; } = new();
}

public sealed record ApiReleaseDto
{
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public required ApiReleaseContract Properties { get; init; }

    public record ApiReleaseContract
    {
        [JsonPropertyName("apiId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? ApiId { get; init; }

        [JsonPropertyName("notes")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Notes { get; init; }
    }
}