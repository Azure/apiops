using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace common;

internal static class JsonNodeExtensions
{
    public static JsonNode Clone(this JsonNode node)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(node);

        return JsonNode.Parse(bytes) ?? throw new InvalidOperationException("Could not deserialize JSON node.");
    }
}

public static class JsonObjectExtensions
{
    public static JsonObject Clone(this JsonObject jsonObject) => JsonNodeExtensions.Clone(jsonObject).AsObject();

    public static JsonNode GetProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.TryGetProperty(propertyName) ?? throw new InvalidOperationException($"Could not find property '{propertyName}' in JSON object.");

    public static JsonNode? TryGetProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.TryGetPropertyValue(propertyName, out var node)
        ? node ?? throw new InvalidOperationException($"Property '{propertyName}' is null.")
        : null;

    public static JsonObject GetJsonObjectProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.GetProperty(propertyName).AsObject();

    public static JsonArray GetJsonArrayProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.GetProperty(propertyName)
                  .AsArray();

    public static string GetStringProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.GetProperty(propertyName)
                  .GetValue<string>();

    public static string? TryGetStringProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.TryGetProperty(propertyName)
                 ?.GetValue<string>();

    public static JsonObject AddProperty(this JsonObject jsonObject, string propertyName, JsonNode? property)
    {
        var clonedObject = jsonObject.Clone();
        clonedObject.Add(propertyName, property);
        return clonedObject;
    }
}