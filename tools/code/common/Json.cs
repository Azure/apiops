using LanguageExt;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace common;

public static class JsonValueExtensions
{
    public static Either<string, string> TryAsString(this JsonValue? jsonValue) =>
        jsonValue is null
        ? Either<string, string>.Left("JSON value is null.")
        : jsonValue.TryGetValue<string>(out var stringValue)
            ? Either<string, string>.Right(stringValue)
            : Either<string, string>.Left("JSON value is not a string.");

    public static Either<string, Uri> TryAsAbsoluteUri(this JsonValue? jsonValue) =>
        jsonValue.TryAsString()
                 .Bind(uriString => Uri.TryCreate(uriString, UriKind.Absolute, out var uri)
                                     ? Either<string, Uri>.Right(uri)
                                     : $"JSON value '{uriString}' is not a valid absolute URI.");

    public static Either<string, int> TryAsInt(this JsonValue? jsonValue) =>
        jsonValue is null
        ? "JSON value is null."
        : jsonValue.TryGetValue<int>(out var intValue)
          || (jsonValue.TryGetValue<string>(out var stringValue) && int.TryParse(stringValue, out intValue))
            ? intValue
            : "JSON value is not an integer.";

    public static Either<string, bool> TryAsBool(this JsonValue? jsonValue) =>
        jsonValue is null
        ? "JSON value is null."
        : jsonValue.TryGetValue<bool>(out var boolValue)
          || (jsonValue.TryGetValue<string>(out var stringValue) && bool.TryParse(stringValue, out boolValue))
            ? boolValue
            : "JSON value is not a boolean.";
}

public static class JsonNodeExtensions
{
    public static Either<string, JsonObject> TryAsJsonObject(this JsonNode? node) =>
        node is JsonObject jsonObject
        ? jsonObject
        : "Node is not a JSON object.";

    public static Either<string, JsonArray> TryAsJsonArray(this JsonNode? node) =>
        node is JsonArray jsonArray
        ? jsonArray
        : "Node is not a JSON array.";

    public static Either<string, JsonValue> TryAsJsonValue(this JsonNode? node) =>
        node is JsonValue jsonValue
        ? jsonValue
        : "Node is not a JSON value.";

    public static Either<string, string> TryAsString(this JsonNode? node) =>
        node.TryAsJsonValue()
            .Bind(jsonValue => jsonValue.TryAsString());

    public static Either<string, Uri> TryAsAbsoluteUri(this JsonNode? node) =>
        node.TryAsJsonValue()
            .Bind(jsonValue => jsonValue.TryAsAbsoluteUri());

    public static Either<string, int> TryAsInt(this JsonNode? node) =>
        node.TryAsJsonValue()
            .Bind(jsonValue => jsonValue.TryAsInt());

    public static Either<string, bool> TryAsBool(this JsonNode? node) =>
        node.TryAsJsonValue()
            .Bind(jsonValue => jsonValue.TryAsBool());
}

public static class JsonArrayExtensions
{
    public static ImmutableArray<JsonObject> PickJsonObjects(this JsonArray jsonArray) =>
        jsonArray.Choose(node => node.TryAsJsonObject().ToOption())
                 .ToImmutableArray();

    public static ImmutableArray<string> PickStrings(this JsonArray jsonArray) =>
        jsonArray.Choose(node => node.TryAsString().ToOption())
                 .ToImmutableArray();

    public static JsonArray ToJsonArray(this IEnumerable<JsonNode> nodes) =>
        new(nodes.ToArray());
}

public static class JsonObjectExtensions
{
    public static JsonSerializerOptions SerializerOptions { get; } = new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static JsonNode GetProperty(this JsonObject? jsonObject, string propertyName) =>
        jsonObject.TryGetProperty(propertyName)
                  .IfLeftThrow();

    public static Either<string, JsonNode> TryGetProperty(this JsonObject? jsonObject, string propertyName) =>
        jsonObject is null
        ? "JSON object is null."
        : jsonObject.TryGetPropertyValue(propertyName, out var property)
            ? property is null
                ? $"Property '{propertyName}' is null."
                : property
            : $"Property '{propertyName}' is missing.";

    private static T IfLeftThrow<T>(this Either<string, T> either) =>
        either.IfLeft(error => throw new JsonException(error));

    public static JsonObject GetJsonObjectProperty(this JsonObject? jsonObject, string propertyName) =>
        jsonObject.TryGetJsonObjectProperty(propertyName)
                  .IfLeftThrow();

    public static Either<string, JsonObject> TryGetJsonObjectProperty(this JsonObject? jsonObject, string propertyName) =>
        jsonObject.TryGetProperty(propertyName)
                  .Bind(JsonNodeExtensions.TryAsJsonObject)
                  .BindPropertyError(propertyName);

    private static Either<string, T> BindPropertyError<T>(this Either<string, T> either, string propertyName) =>
        either.MapLeft(error => $"Property '{propertyName}' is invalid. {error}");

    public static Either<string, JsonArray> TryGetJsonArrayProperty(this JsonObject? jsonObject, string propertyName) =>
        jsonObject.TryGetProperty(propertyName)
                  .Bind(JsonNodeExtensions.TryAsJsonArray)
                  .BindPropertyError(propertyName);

    public static Uri GetAbsoluteUriProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.TryGetAbsoluteUriProperty(propertyName)
                  .IfLeftThrow();

    public static Either<string, Uri> TryGetAbsoluteUriProperty(this JsonObject? jsonObject, string propertyName) =>
        jsonObject.TryGetProperty(propertyName)
                  .Bind(JsonNodeExtensions.TryAsAbsoluteUri)
                  .BindPropertyError(propertyName);

    public static Either<string, string> TryGetStringProperty(this JsonObject? jsonObject, string propertyName) =>
        jsonObject.TryGetProperty(propertyName)
                  .Bind(JsonNodeExtensions.TryAsString)
                  .BindPropertyError(propertyName);

    public static string GetNonEmptyOrWhiteSpaceStringProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.TryGetNonEmptyOrWhiteSpaceStringProperty(propertyName)
                  .IfLeftThrow();

    public static Either<string, string> TryGetNonEmptyOrWhiteSpaceStringProperty(this JsonObject? jsonObject, string propertyName) =>
        jsonObject.TryGetProperty(propertyName)
                  .Bind(JsonNodeExtensions.TryAsString)
                  .Bind(value => string.IsNullOrWhiteSpace(value)
                                    ? Either<string, string>.Left($"Property '{propertyName}' is empty or whitespace.")
                                    : Either<string, string>.Right(value))
                  .BindPropertyError(propertyName);

    public static string GetStringProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.TryGetStringProperty(propertyName)
                  .IfLeftThrow();

    public static int GetIntProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.TryGetIntProperty(propertyName)
                  .IfLeftThrow();

    public static Either<string, int> TryGetIntProperty(this JsonObject? jsonObject, string propertyName) =>
        jsonObject.TryGetProperty(propertyName)
                  .Bind(JsonNodeExtensions.TryAsInt)
                  .BindPropertyError(propertyName);

    public static bool GetBoolProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.TryGetBoolProperty(propertyName)
                  .IfLeftThrow();

    public static Either<string, bool> TryGetBoolProperty(this JsonObject? jsonObject, string propertyName) =>
        jsonObject.TryGetProperty(propertyName)
                  .Bind(JsonNodeExtensions.TryAsBool)
                  .BindPropertyError(propertyName);

    public static JsonObject Parse<T>(T obj) =>
        TryParse(obj)
            .IfLeft(() => throw new JsonException($"Could not parse {typeof(T).Name} as a JSON object."));

    public static Either<string, JsonObject> TryParse<T>(T obj) =>
        JsonSerializer.SerializeToNode(obj, SerializerOptions)
                      .TryAsJsonObject();

    [return: NotNullIfNotNull(nameof(jsonObject))]
    public static JsonObject? SetProperty(this JsonObject? jsonObject, string propertyName, JsonNode? jsonNode)
    {
        if (jsonObject is null)
        {
            return null;
        }
        else
        {
            jsonObject[propertyName] = jsonNode;
            return jsonObject;
        }
    }
}