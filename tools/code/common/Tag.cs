using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace common;

public sealed record TagsUri : IArtifactUri
{
    public Uri Uri { get; }

    public TagsUri(ServiceUri serviceUri)
    {
        Uri = serviceUri.AppendPath("tags");
    }
}

public sealed record TagsDirectory : IArtifactDirectory
{
    public static string Name { get; } = "tags";

    public ArtifactPath Path { get; }

    public ServiceDirectory ServiceDirectory { get; }

    public TagsDirectory(ServiceDirectory serviceDirectory)
    {
        Path = serviceDirectory.Path.Append(Name);
        ServiceDirectory = serviceDirectory;
    }
}

public sealed record TagName
{
    private readonly string value;

    public TagName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Tag name cannot be null or whitespace.", nameof(value));
        }

        this.value = value;
    }

    public override string ToString() => value;
}

public sealed record TagUri : IArtifactUri
{
    public Uri Uri { get; }

    public TagUri(TagName tagName, TagsUri tagsUri)
    {
        Uri = tagsUri.AppendPath(tagName.ToString());
    }
}

public sealed record TagDirectory : IArtifactDirectory
{
    public ArtifactPath Path { get; }

    public TagsDirectory TagsDirectory { get; }

    public TagDirectory(TagName tagName, TagsDirectory tagsDirectory)
    {
        Path = tagsDirectory.Path.Append(tagName.ToString());
        TagsDirectory = tagsDirectory;
    }
}

public sealed record TagInformationFile : IArtifactFile
{
    public static string Name { get; } = "tagInformation.json";

    public ArtifactPath Path { get; }

    public TagDirectory TagDirectory { get; }

    public TagInformationFile(TagDirectory tagDirectory)
    {
        Path = tagDirectory.Path.Append(Name);
        TagDirectory = tagDirectory;
    }
}

public sealed record TagModel
{
    public required string Name { get; init; }

    public required TagContractProperties Properties { get; init; }

    public sealed record TagContractProperties
    {
        public string? DisplayName { get; init; }

        public JsonObject Serialize() =>
            new JsonObject()
                .AddPropertyIfNotNull("displayName", DisplayName);

        public static TagContractProperties Deserialize(JsonObject jsonObject) =>
            new()
            {
                DisplayName = jsonObject.TryGetStringProperty("displayName")
            };
    }

    public JsonObject Serialize() =>
        new JsonObject()
            .AddProperty("properties", Properties.Serialize());

    public static TagModel Deserialize(TagName name, JsonObject jsonObject) =>
        new()
        {
            Name = jsonObject.TryGetStringProperty("name") ?? name.ToString(),
            Properties = jsonObject.GetJsonObjectProperty("properties")
                                   .Map(TagContractProperties.Deserialize)!
        };
}