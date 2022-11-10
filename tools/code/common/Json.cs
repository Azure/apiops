using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace common;

internal static class JsonNodeExtensions
{
    public static JsonNode Clone(this JsonNode node)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(node);

        return JsonNode.Parse(bytes) ?? throw new InvalidOperationException("Could not deserialize JSON node.");
    }

    public static JsonValue? TryAsJsonValue(this JsonNode node) => node as JsonValue;

    public static string AsString(this JsonNode node, string errorMessage) =>
        node.TryAsString() ?? throw new InvalidOperationException(errorMessage);

    public static string? TryAsString(this JsonNode node) =>
        node.TryAsJsonValue()
            .Map(jsonValue => jsonValue.TryGetValue<string>(out var stringValue)
                              ? stringValue
                              : jsonValue.ToJsonElement().ValueKind is JsonValueKind.String or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number
                                ? jsonValue.ToString()
                                : null);

    public static bool AsBool(this JsonNode node, string errorMessage) =>
        node.TryAsBool() ?? throw new InvalidOperationException(errorMessage);

    public static bool? TryAsBool(this JsonNode node) =>
        node.TryAsJsonValue()
            .Map(jsonValue => jsonValue.TryGetValue<bool>(out var boolValue)
                              ? boolValue
                              : jsonValue.TryGetValue<string>(out var stringValue)
                                ? bool.TryParse(stringValue, out boolValue)
                                  ? boolValue
                                  : default(bool?)
                                : default(bool?));

    public static int AsInt(this JsonNode node, string errorMessage) =>
        node.TryAsInt() ?? throw new InvalidOperationException(errorMessage);

    public static int? TryAsInt(this JsonNode node) =>
        node.TryAsJsonValue()
            .Map(jsonValue => jsonValue.TryGetValue<int>(out var intValue)
                              ? intValue
                              : jsonValue.TryGetValue<string>(out var stringValue)
                                ? int.TryParse(stringValue, out intValue)
                                  ? intValue
                                  : default(int?)
                                : default(int?));
    
    public static double AsDouble(this JsonNode node, string errorMessage) =>
        node.TryAsDouble() ?? throw new InvalidOperationException(errorMessage);

    public static double? TryAsDouble(this JsonNode node) =>
        node.TryAsJsonValue()
            .Map(jsonValue => jsonValue.TryGetValue<double>(out var doubleValue)
                              ? doubleValue
                              : jsonValue.TryGetValue<string>(out var stringValue)
                                ? double.TryParse(stringValue, out doubleValue)
                                  ? doubleValue
                                  : default(double?)
                                : default(double?));

    private static JsonElement ToJsonElement(this JsonNode node) => JsonSerializer.SerializeToElement(node);

    public static JsonNode FromString(string value) =>
        (JsonNode?)value ?? throw new InvalidOperationException("JSON node cannot be null if string is not null.");
}

public static class JsonObjectExtensions
{
    public static JsonObject Clone(this JsonObject jsonObject) => JsonNodeExtensions.Clone(jsonObject).AsObject();

    public static JsonNode GetProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.GetNullableProperty(propertyName) ?? throw new InvalidOperationException($"Property '{propertyName}' is null.");

    public static JsonNode? GetNullableProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.TryGetPropertyValue(propertyName, out var node)
            ? node
            : throw new InvalidOperationException($"Could not find property '{propertyName}' in JSON object.");

    public static JsonNode? TryGetProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.TryGetPropertyValue(propertyName, out var node)
            ? node
            : null;

    public static JsonObject GetJsonObjectProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.GetProperty(propertyName)
                  .AsObject();

    public static JsonObject? GetNullableJsonObjectProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.GetNullableProperty(propertyName)
                  .Map(node => node.AsObject());

    public static JsonObject? TryGetJsonObjectProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.TryGetProperty(propertyName) is JsonObject property
            ? property
            : null;

    public static JsonArray GetJsonArrayProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.GetProperty(propertyName)
                  .AsArray();

    public static JsonArray? GetNullableJsonArrayProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.GetNullableProperty(propertyName)
                  .Map(node => node.AsArray());

    public static JsonArray? TryGetJsonArrayProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.TryGetProperty(propertyName) is JsonArray property
            ? property
            : null;

    public static JsonValue GetJsonValueProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.GetProperty(propertyName)
                  .AsValue();

    public static JsonValue? GetNullableJsonValueProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.GetNullableProperty(propertyName)
                  .Map(node => node.AsValue());

    public static JsonValue? TryGetJsonValueProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.TryGetProperty(propertyName) is JsonValue property
            ? property
            : null;

    public static string GetStringProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.GetJsonValueProperty(propertyName)
                  .AsString($"Property '{propertyName}''s value cannot be converted to string.");

    public static string? TryGetStringProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.TryGetJsonValueProperty(propertyName)
                  .Bind(JsonNodeExtensions.TryAsString);

    public static bool GetBoolProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.GetJsonValueProperty(propertyName)
                  .AsBool($"Property '{propertyName}''s value cannot be converted to bool.");

    public static bool? TryGetBoolProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.TryGetJsonValueProperty(propertyName)
                  .Bind(JsonNodeExtensions.TryAsBool);

    public static int GetIntProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.GetJsonValueProperty(propertyName)
                  .AsInt($"Property '{propertyName}''s value cannot be converted to int.");

    public static int? TryGetIntProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.TryGetJsonValueProperty(propertyName)
                  .Bind(JsonNodeExtensions.TryAsInt);

    public static double GetDoubleProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.GetJsonValueProperty(propertyName)
                  .AsDouble($"Property '{propertyName}''s value cannot be converted to double.");

    public static double? TryGetDoubleProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.TryGetJsonValueProperty(propertyName)
                  .Bind(JsonNodeExtensions.TryAsDouble);

    public static JsonObject AddPropertyIfNotNull(this JsonObject jsonObject, string propertyName, JsonNode? property) =>
        property is not null
            ? jsonObject.AddProperty(propertyName, property)
            : jsonObject;

    public static JsonObject AddPropertyIfNotEmpty(this JsonObject jsonObject, string propertyName, JsonArray property) =>
        property.Any()
            ? jsonObject.AddProperty(propertyName, property)
            : jsonObject;

    public static JsonObject AddProperty(this JsonObject jsonObject, string propertyName, JsonNode? property)
    {
        var clonedObject = jsonObject.Clone();
        clonedObject.Add(propertyName, property);
        return clonedObject;
    }

    public static JsonObject Serialize<TKey, TValue>(IDictionary<TKey, TValue> dictionary) =>
        JsonSerializer.SerializeToNode(dictionary)
                     ?.AsObject()
        ?? throw new InvalidOperationException("Failed to serialize dictionary.");

    public static JsonObject ToJsonObject(this IEnumerable<KeyValuePair<string, string[]>> dictionary)
    {
        var jsonObject = new JsonObject();

        dictionary.Select(kvp => (kvp.Key,
                                  Value: kvp.Value.Select(JsonNodeExtensions.FromString)
                                                  .ToJsonArray()))
                  .ForEach(kvp => jsonObject.Add(kvp.Key, kvp.Value));

        return jsonObject;
    }
}

public static class JsonArrayExtensions
{
    public static async ValueTask<JsonArray> ToJsonArray(this IAsyncEnumerable<JsonNode> nodes, CancellationToken cancellationToken)
    {
        var nodesList = await nodes.ToListAsync(cancellationToken);
        return nodesList.ToJsonArray();
    }

    public static JsonArray ToJsonArray(this IEnumerable<JsonNode> nodes) => new(nodes.ToArray());
}