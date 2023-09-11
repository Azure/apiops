using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace common;

public sealed record ApiSchemasUri : IArtifactUri
{
    public Uri Uri { get; }

    public ApiSchemasUri(ApiUri apiUri)
    {
        Uri = apiUri.AppendPath("schemas");
    }
}

public sealed record ApiSchemaName
{
    private readonly string value;

    public ApiSchemaName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"API schema name cannot be null or whitespace.", nameof(value));
        }

        this.value = value;
    }

    public override string ToString() => value;

    public static ApiSchemaName GraphQl { get; } = new("graphql");
}

public sealed record ApiSchemaUri : IArtifactUri
{
    public Uri Uri { get; }

    public ApiSchemaUri(ApiSchemaName apiSchemaName, ApiSchemasUri apiSchemasUri)
    {
        Uri = apiSchemasUri.AppendPath(apiSchemaName.ToString());
    }
}

public sealed record ApiSchemaModel
{
    public required string Name { get; init; }

    public required SchemaContractProperties Properties { get; init; }

    public sealed record SchemaContractProperties
    {
        public string? ContentType { get; init; }
        public SchemaDocumentProperties? Document { get; init; }

        public JsonObject Serialize() =>
            new JsonObject()
                .AddPropertyIfNotNull("contentType", ContentType)
                .AddPropertyIfNotNull("document", Document?.Serialize());

        public static SchemaContractProperties Deserialize(JsonObject jsonObject) =>
            new()
            {
                ContentType = jsonObject.TryGetStringProperty("contentType"),
                Document = jsonObject.TryGetJsonObjectProperty("document")
                                     .Map(SchemaDocumentProperties.Deserialize)
            };

        public sealed record SchemaDocumentProperties
        {
            public string? Value { get; init; }

            public JsonObject Serialize() =>
                new JsonObject()
                    .AddPropertyIfNotNull("value", Value);

            public static SchemaDocumentProperties Deserialize(JsonObject jsonObject) =>
                new()
                {
                    Value = jsonObject.TryGetStringProperty("value")
                };
        }
    }

    public JsonObject Serialize() =>
        new JsonObject()
            .AddProperty("properties", Properties.Serialize());

    public static ApiSchemaModel Deserialize(ApiSchemaName name, JsonObject jsonObject) =>
        new()
        {
            Name = jsonObject.TryGetStringProperty("name") ?? name.ToString(),
            Properties = jsonObject.GetJsonObjectProperty("properties")
                                   .Map(SchemaContractProperties.Deserialize)!
        };
}