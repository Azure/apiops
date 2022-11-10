using System;
using System.Linq;
using System.Text.Json.Nodes;

namespace common;

public sealed record NamedValuesUri : IArtifactUri
{
    public Uri Uri { get; }

    public NamedValuesUri(ServiceUri serviceUri)
    {
        Uri = serviceUri.AppendPath("namedValues");
    }
}

public sealed record NamedValuesDirectory : IArtifactDirectory
{
    public static string Name { get; } = "named values";

    public ArtifactPath Path { get; }

    public ServiceDirectory ServiceDirectory { get; }

    public NamedValuesDirectory(ServiceDirectory serviceDirectory)
    {
        Path = serviceDirectory.Path.Append(Name);
        ServiceDirectory = serviceDirectory;
    }
}

public sealed record NamedValueName
{
    private readonly string value;

    public NamedValueName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Named value name cannot be null or whitespace.", nameof(value));
        }

        this.value = value;
    }

    public override string ToString() => value;
}

public sealed record NamedValueUri : IArtifactUri
{
    public Uri Uri { get; }

    public NamedValueUri(NamedValueName namedValueName, NamedValuesUri namedValuesUri)
    {
        Uri = namedValuesUri.AppendPath(namedValueName.ToString());
    }
}

public sealed record NamedValueDirectory : IArtifactDirectory
{
    public ArtifactPath Path { get; }

    public NamedValuesDirectory NamedValuesDirectory { get; }

    public NamedValueDirectory(NamedValueName namedValueName, NamedValuesDirectory namedValuesDirectory)
    {
        Path = namedValuesDirectory.Path.Append(namedValueName.ToString());
        NamedValuesDirectory = namedValuesDirectory;
    }
}

public sealed record NamedValueInformationFile : IArtifactFile
{
    public static string Name { get; } = "namedValueInformation.json";

    public ArtifactPath Path { get; }

    public NamedValueDirectory NamedValueDirectory { get; }

    public NamedValueInformationFile(NamedValueDirectory namedValueDirectory)
    {
        Path = namedValueDirectory.Path.Append(Name);
        NamedValueDirectory = namedValueDirectory;
    }
}

public sealed record NamedValueModel
{
    public required string Name { get; init; }

    public required NamedValueContractProperties Properties { get; init; }

    public sealed record NamedValueContractProperties
    {
        public string? DisplayName { get; init; }
        public KeyVaultContract? KeyVault { get; init; }
        public bool? Secret { get; init; }
        public string[]? Tags { get; init; }
        public string? Value { get; init; }

        public JsonObject Serialize() =>
            new JsonObject()
                .AddPropertyIfNotNull("displayName", DisplayName)
                .AddPropertyIfNotNull("keyVault", KeyVault?.Serialize())
                .AddPropertyIfNotNull("secret", Secret)
                .AddPropertyIfNotNull("tags", Tags?.Choose(tag => (JsonNode?)tag)
                                                  ?.ToJsonArray())
                .AddPropertyIfNotNull("value", Value);

        public static NamedValueContractProperties Deserialize(JsonObject jsonObject) =>
            new()
            {
                DisplayName = jsonObject.TryGetStringProperty("displayName"),
                KeyVault = jsonObject.TryGetJsonObjectProperty("keyVault")
                                         .Map(KeyVaultContract.Deserialize),
                Secret = jsonObject.TryGetBoolProperty("secret"),
                Tags = jsonObject.TryGetJsonArrayProperty("tags")
                                 .Map(jsonArray => jsonArray.Choose(node => node?.GetValue<string>())
                                                            .ToArray()),
                Value = jsonObject.TryGetStringProperty("value")
            };

        public sealed record KeyVaultContract
        {
            public string? IdentityClientId { get; init; }
            public string? SecretIdentifier { get; init; }

            public JsonObject Serialize() =>
                new JsonObject()
                    .AddPropertyIfNotNull("identityClientId", IdentityClientId)
                    .AddPropertyIfNotNull("secretIdentifier", SecretIdentifier);

            public static KeyVaultContract Deserialize(JsonObject jsonObject) =>
                new()
                {
                    IdentityClientId = jsonObject.TryGetStringProperty("identityClientId"),
                    SecretIdentifier = jsonObject.TryGetStringProperty("secretIdentifier")
                };
        }
    }

    public JsonObject Serialize() =>
        new JsonObject()
            .AddProperty("properties", Properties.Serialize());

    public static NamedValueModel Deserialize(NamedValueName name, JsonObject jsonObject) =>
        new()
        {
            Name = jsonObject.TryGetStringProperty("name") ?? name.ToString(),
            Properties = jsonObject.GetJsonObjectProperty("properties")
                                   .Map(NamedValueContractProperties.Deserialize)!
        };
}