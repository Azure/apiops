using System;
using System.Collections.Generic;
using System.Linq;
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

    public static JsonNode? GetNullableProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.TryGetNullableProperty(propertyName) ?? throw new InvalidOperationException($"Could not find property '{propertyName}' in JSON object.");

    public static JsonNode? TryGetProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.TryGetPropertyValue(propertyName, out var node)
        ? node ?? throw new InvalidOperationException($"Property '{propertyName}' is null.")
        : null;

    public static JsonNode? TryGetNullableProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.TryGetPropertyValue(propertyName, out var node)
        ? node
        : null;

    public static JsonObject GetJsonObjectProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.GetProperty(propertyName).AsObject();

    public static JsonObject? GetNullableJsonObjectProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.GetNullableProperty(propertyName)?.AsObject();

    public static JsonObject? TryGetJsonObjectProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.TryGetProperty(propertyName)?.AsObject();

    public static JsonObject? TryGetNullableJsonObjectProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.TryGetNullableProperty(propertyName)?.AsObject();

    public static T GetAndMapJsonObjectProperty<T>(this JsonObject jsonObject, string propertyName, Func<JsonObject, T> mapper) =>
        mapper(jsonObject.GetJsonObjectProperty(propertyName));

    public static T? TryGetAndMapJsonObjectProperty<T>(this JsonObject jsonObject, string propertyName, Func<JsonObject, T> mapper)
    {
        var propertyObject = jsonObject.TryGetJsonObjectProperty(propertyName);

        return propertyObject is null ? default : mapper(propertyObject);
    }

    public static T? TryGetAndMapNullableJsonObjectProperty<T>(this JsonObject jsonObject, string propertyName, Func<JsonObject, T> mapper)
    {
        var propertyObject = jsonObject.TryGetNullableJsonObjectProperty(propertyName);

        return propertyObject is null ? default : mapper(propertyObject);
    }

    public static JsonArray GetJsonArrayProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.GetProperty(propertyName)
                  .AsArray();

    public static JsonArray? GetNullableJsonArrayProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.GetNullableProperty(propertyName)
                 ?.AsArray();

    public static JsonArray? TryGetJsonArrayProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.TryGetProperty(propertyName)?.AsArray();

    public static JsonArray? TryGetNullableJsonArrayProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.TryGetNullableProperty(propertyName)?.AsArray();

    public static T[]? TryGetAndMapNullableJsonArrayProperty<T>(this JsonObject jsonObject, string propertyName, Func<JsonNode, T> mapper) =>
        jsonObject.TryGetNullableJsonArrayProperty(propertyName)
                 ?.Where(node => node is not null)
                 ?.Select(node => mapper(node!))
                  .ToArray();

    public static string GetStringProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.GetProperty(propertyName)
                  .GetValue<string>();

    public static string? GetNullableStringProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.GetNullableProperty(propertyName)
                 ?.GetValue<string>();

    public static string? TryGetStringProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.TryGetProperty(propertyName)
                 ?.GetValue<string>();

    public static string? TryGetNullableStringProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.TryGetNullableProperty(propertyName)
                 ?.GetValue<string>();

    public static int GetIntProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.GetProperty(propertyName)
                  .GetValue<int>();

    public static int? GetNullableIntProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.GetNullableProperty(propertyName)
                 ?.GetValue<int>();

    public static int? TryGetIntProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.TryGetProperty(propertyName)
                 ?.GetValue<int>();

    public static int? TryGetNullableIntProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.TryGetNullableProperty(propertyName)
                 ?.GetValue<int>();

    public static bool GetBoolProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.GetProperty(propertyName)
                  .GetValue<bool>();

    public static bool? GetNullableBoolProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.GetNullableProperty(propertyName)
                 ?.GetValue<bool>();

    public static bool? TryGetBoolProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.TryGetProperty(propertyName)
                 ?.GetValue<bool>();

    public static bool? TryGetNullableBoolProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.TryGetNullableProperty(propertyName)
                 ?.GetValue<bool>();

    public static JsonObject AddProperty(this JsonObject jsonObject, string propertyName, JsonNode? property)
    {
        var clonedObject = jsonObject.Clone();
        clonedObject.Add(propertyName, property);
        return clonedObject;
    }

    public static JsonObject AddPropertyIfNotNull(this JsonObject jsonObject, string propertyName, JsonNode? property) =>
        property is null
        ? jsonObject
        : jsonObject.AddProperty(propertyName, property);
}

public static class JsonArrayExtensions
{
    public static JsonArray ToJsonArray<T>(this IEnumerable<T> enumerable, Func<T, JsonNode?> mapper) =>
        new(enumerable.Select(mapper).ToArray());
}
