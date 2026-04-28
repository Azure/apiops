using System;
using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace common;

public sealed record WorkspaceNamedValueResource : IResourceWithInformationFile, IChildResource
{
    private WorkspaceNamedValueResource() { }

    public string FileName { get; } = "namedValueInformation.json";

    public string CollectionDirectoryName { get; } = "named values";

    public string SingularName { get; } = "named value";

    public string PluralName { get; } = "named values";

    public string CollectionUriPath { get; } = "namedValues";

    public Type DtoType { get; } = typeof(WorkspaceNamedValueDto);

    public IResource Parent { get; } = WorkspaceResource.Instance;

    public static WorkspaceNamedValueResource Instance { get; } = new();
}

public sealed record WorkspaceNamedValueDto
{
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public required NamedValueContract Properties { get; init; }

    public sealed record NamedValueContract
    {
        [JsonPropertyName("displayName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? DisplayName { get; init; }

        [JsonPropertyName("keyVault")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public KeyVaultContract? KeyVault { get; init; }

        [JsonPropertyName("secret")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool? Secret { get; init; }

        [JsonPropertyName("tags")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public ImmutableArray<string>? Tags { get; init; }

        [JsonPropertyName("value")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Value { get; init; }
    }

    public sealed record KeyVaultContract
    {
        [JsonPropertyName("identityClientId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? IdentityClientId { get; init; }

        [JsonPropertyName("secretIdentifier")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? SecretIdentifier { get; init; }
    }
}