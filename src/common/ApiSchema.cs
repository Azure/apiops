using System;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace common;

public sealed record ApiSchemaResource : IResourceWithDto, IChildResource
{
    private ApiSchemaResource() { }

    public string FileName { get; } = "apiSchemaInformation.json";

    public string CollectionDirectoryName { get; } = "schemas";

    public string SingularName { get; } = "schema";

    public string PluralName { get; } = "schemas";

    public string CollectionUriPath { get; } = "schemas";

    public Type DtoType { get; } = typeof(ApiSchemaDto);

    public IResource Parent { get; } = ApiResource.Instance;

    public static ApiSchemaResource Instance { get; } = new();
}

public sealed record ApiSchemaDto
{
    [JsonPropertyName("properties")]
    public required ApiSchemaContract Properties { get; init; }

    public sealed record ApiSchemaContract
    {
        [JsonPropertyName("contentType")]
        public required string ContentType { get; init; }

        [JsonPropertyName("document")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public SchemaDocumentProperties? Document { get; init; }
    }

    public sealed record SchemaDocumentProperties
    {
        [JsonPropertyName("components")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public object? Components { get; init; }

        [JsonPropertyName("definitions")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public object? Definitions { get; init; }

        [JsonPropertyName("value")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Value { get; init; }

        [JsonPropertyName("odata")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? OData { get; init; }
    }
}

public static class ApiSchemaExtensions
{
    public static bool IsODataSchema(this JsonObject schemaJson) =>
        schemaJson.TryGetPropertyValue("properties", out var properties)
        && properties is JsonObject propsObj
        && propsObj.TryGetPropertyValue("contentType", out var contentType)
        && contentType?.GetValue<string>() is "application/vnd.ms-azure-apim.odata.schema";
}
