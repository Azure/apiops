namespace common.codegen;

internal sealed class NamedValue : IResourceWithDirectory, IResourceWithInformationFile
{
    public string NameType { get; } = "NamedValueName";
    public string CollectionDirectoryType { get; } = "NamedValuesDirectory";
    public string CollectionDirectoryName { get; } = "named values";
    public string DirectoryType { get; } = "NamedValueDirectory";
    public string CollectionUriType { get; } = "NamedValuesUri";
    public string CollectionUriPath { get; } = "namedValues";
    public string UriType { get; } = "NamedValueUri";
    public string ModuleType { get; } = "NamedValueModule";

    public string InformationFileType { get; } = "NamedValueInformationFile";

    public string InformationFileName { get; } = "namedValueInformation.json";

    public string DtoType { get; } = "NamedValueDto";

    public string DtoCode { get; } = """
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
""";
}
