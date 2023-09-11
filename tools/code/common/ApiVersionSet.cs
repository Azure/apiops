using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace common;

public sealed record ApiVersionSetsUri : IArtifactUri
{
    public Uri Uri { get; }

    public ApiVersionSetsUri(ServiceUri serviceUri)
    {
        Uri = serviceUri.AppendPath("apiVersionSets");
    }
}

public sealed record ApiVersionSetsDirectory : IArtifactDirectory
{
    public static string Name { get; } = "version sets";

    public ArtifactPath Path { get; }

    public ServiceDirectory ServiceDirectory { get; }

    public ApiVersionSetsDirectory(ServiceDirectory serviceDirectory)
    {
        Path = serviceDirectory.Path.Append(Name);
        ServiceDirectory = serviceDirectory;
    }
}

public sealed record ApiVersionSetName
{
    private readonly string value;

    public ApiVersionSetName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"API version set name cannot be null or whitespace.", nameof(value));
        }

        this.value = value;
    }

    public override string ToString() => value;
}

public sealed record ApiVersionSetUri : IArtifactUri
{
    public Uri Uri { get; }

    public ApiVersionSetUri(ApiVersionSetName apiVersionSetName, ApiVersionSetsUri apiVersionSetsUri)
    {
        Uri = apiVersionSetsUri.AppendPath(apiVersionSetName.ToString());
    }
}

public sealed record ApiVersionSetDirectory : IArtifactDirectory
{
    public ArtifactPath Path { get; }

    public ApiVersionSetsDirectory ApiVersionSetsDirectory { get; }

    public ApiVersionSetDirectory(ApiVersionSetName apiVersionSetName, ApiVersionSetsDirectory apiVersionSetsDirectory)
    {
        Path = apiVersionSetsDirectory.Path.Append(apiVersionSetName.ToString());
        ApiVersionSetsDirectory = apiVersionSetsDirectory;
    }
}

public sealed record ApiVersionSetInformationFile : IArtifactFile
{
    public static string Name { get; } = "apiVersionSetInformation.json";

    public ArtifactPath Path { get; }

    public ApiVersionSetDirectory ApiVersionSetDirectory { get; }

    public ApiVersionSetInformationFile(ApiVersionSetDirectory apiVersionSetDirectory)
    {
        Path = apiVersionSetDirectory.Path.Append(Name);
        ApiVersionSetDirectory = apiVersionSetDirectory;
    }
}

public sealed record ApiVersionSetApisUri : IArtifactUri
{
    public Uri Uri { get; }

    public ApiVersionSetApisUri(ApiVersionSetUri apiVersionSetUri)
    {
        Uri = apiVersionSetUri.AppendPath("apis");
    }
}

public sealed record ApiVersionSetApisFile : IArtifactFile
{
    private static readonly string name = "apis.json";

    public ArtifactPath Path { get; }

    public ApiVersionSetDirectory ApiVersionSetDirectory { get; }

    public ApiVersionSetApisFile(ApiVersionSetDirectory apiVersionSetDirectory)
    {
        Path = apiVersionSetDirectory.Path.Append(name);
        ApiVersionSetDirectory = apiVersionSetDirectory;
    }
}

public sealed record ApiVersionSetModel
{
    public required string Name { get; init; }

    public required ApiVersionSetContractProperties Properties { get; init; }

    public sealed record ApiVersionSetContractProperties
    {
        public string? Description { get; init; }
        public string? DisplayName { get; init; }
        public string? VersionHeaderName { get; init; }
        public string? VersionQueryName { get; init; }
        public VersioningSchemeOption? VersioningScheme { get; init; }

        public JsonObject Serialize() =>
            new JsonObject()
                .AddPropertyIfNotNull("description", Description)
                .AddPropertyIfNotNull("displayName", DisplayName)
                .AddPropertyIfNotNull("versionHeaderName", VersionHeaderName)
                .AddPropertyIfNotNull("versionQueryName", VersionQueryName)
                .AddPropertyIfNotNull("versioningScheme", VersioningScheme?.Serialize());

        public static ApiVersionSetContractProperties Deserialize(JsonObject jsonObject) =>
            new()
            {
                Description = jsonObject.TryGetStringProperty("description"),
                DisplayName = jsonObject.TryGetStringProperty("displayName"),
                VersionHeaderName = jsonObject.TryGetStringProperty("versionHeaderName"),
                VersionQueryName = jsonObject.TryGetStringProperty("versionQueryName"),
                VersioningScheme = jsonObject.TryGetProperty("versioningScheme")
                                             .Map(VersioningSchemeOption.Deserialize)
            };

        public sealed record VersioningSchemeOption
        {
            private readonly string value;

            private VersioningSchemeOption(string value)
            {
                this.value = value;
            }

            public static VersioningSchemeOption Header => new("Header");
            public static VersioningSchemeOption Query => new("Query");
            public static VersioningSchemeOption Segment => new("Segment");

            public override string ToString() => value;

            public JsonNode Serialize() => JsonValue.Create(ToString()) ?? throw new JsonException("Value cannot be null.");

            public static VersioningSchemeOption Deserialize(JsonNode node) =>
                node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var value)
                    ? value switch
                    {
                        _ when nameof(Header).Equals(value, StringComparison.OrdinalIgnoreCase) => Header,
                        _ when nameof(Query).Equals(value, StringComparison.OrdinalIgnoreCase) => Query,
                        _ when nameof(Segment).Equals(value, StringComparison.OrdinalIgnoreCase) => Segment,
                        _ => throw new JsonException($"'{value}' is not a valid {nameof(VersioningSchemeOption)}.")
                    }
                        : throw new JsonException("Node must be a string JSON value.");
        }
    }

    public JsonObject Serialize() =>
        new JsonObject()
            .AddProperty("properties", Properties.Serialize());

    public static ApiVersionSetModel Deserialize(ApiVersionSetName name, JsonObject jsonObject) =>
        new()
        {
            Name = jsonObject.TryGetStringProperty("name") ?? name.ToString(),
            Properties = jsonObject.GetJsonObjectProperty("properties")
                                   .Map(ApiVersionSetContractProperties.Deserialize)!
        };
}