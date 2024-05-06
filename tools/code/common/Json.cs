using LanguageExt;
using LanguageExt.UnsafeValueAccess;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public static class JsonArrayExtensions
{
    public static JsonArray ToJsonArray(this IEnumerable<JsonNode?> nodes) =>
        nodes.Aggregate(new JsonArray(),
                       (array, node) =>
                       {
                           array.Add(node);
                           return array;
                       });

    public static ValueTask<JsonArray> ToJsonArray(this IAsyncEnumerable<JsonNode?> nodes, CancellationToken cancellationToken) =>
        nodes.AggregateAsync(new JsonArray(),
                            (array, node) =>
                            {
                                array.Add(node);
                                return array;
                            },
                            cancellationToken);

    public static ImmutableArray<JsonObject> GetJsonObjects(this JsonArray jsonArray) =>
        jsonArray.Choose(node => node.TryAsJsonObject())
                 .ToImmutableArray();

    public static ImmutableArray<JsonArray> GetJsonArrays(this JsonArray jsonArray) =>
        jsonArray.Choose(node => node.TryAsJsonArray())
                 .ToImmutableArray();

    public static ImmutableArray<JsonValue> GetJsonValues(this JsonArray jsonArray) =>
        jsonArray.Choose(node => node.TryAsJsonValue())
                 .ToImmutableArray();

    public static ImmutableArray<string> GetNonEmptyOrWhitespaceStrings(this JsonArray jsonArray) =>
        jsonArray.Choose(node => node.TryAsString())
                 .Where(value => !string.IsNullOrWhiteSpace(value))
                 .ToImmutableArray();
}

public static class JsonNodeExtensions
{
    public static JsonNodeOptions Options { get; } = new() { PropertyNameCaseInsensitive = true };

    public static Option<JsonObject> TryAsJsonObject(this JsonNode? node) =>
        node is JsonObject jsonObject
            ? jsonObject
            : Option<JsonObject>.None;

    public static Option<JsonArray> TryAsJsonArray(this JsonNode? node) =>
        node is JsonArray jsonArray
            ? jsonArray
            : Option<JsonArray>.None;

    public static Option<JsonValue> TryAsJsonValue(this JsonNode? node) =>
        node is JsonValue jsonValue
            ? jsonValue
            : Option<JsonValue>.None;

    public static Option<string> TryAsString(this JsonNode? node) =>
        node.TryAsJsonValue()
            .Bind(JsonValueExtensions.TryAsString);

    public static Option<Guid> TryAsGuid(this JsonNode? node) =>
        node.TryAsJsonValue()
            .Bind(JsonValueExtensions.TryAsGuid);

    public static Option<Uri> TryAsAbsoluteUri(this JsonNode? node) =>
        node.TryAsJsonValue()
            .Bind(JsonValueExtensions.TryAsAbsoluteUri);

    public static Option<DateTimeOffset> TryAsDateTimeOffset(this JsonNode? node) =>
        node.TryAsJsonValue()
            .Bind(JsonValueExtensions.TryAsDateTimeOffset);

    public static Option<DateTime> TryAsDateTime(this JsonNode? node) =>
        node.TryAsJsonValue()
            .Bind(JsonValueExtensions.TryAsDateTime);

    public static Option<int> TryAsInt(this JsonNode? node) =>
        node.TryAsJsonValue()
            .Bind(JsonValueExtensions.TryAsInt);

    public static Option<double> TryAsDouble(this JsonNode? node) =>
        node.TryAsJsonValue()
            .Bind(JsonValueExtensions.TryAsDouble);

    public static Option<bool> TryAsBool(this JsonNode? node) =>
        node.TryAsJsonValue()
            .Bind(JsonValueExtensions.TryAsBool);
}

public static class JsonObjectExtensions
{
    public static JsonSerializerOptions SerializerOptions { get; } = new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static JsonNode GetProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.TryGetProperty(propertyName)
                  .IfLeftThrowJsonException();

    public static Option<JsonNode> GetOptionalProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.TryGetProperty(propertyName)
                  .ToOption();

    public static Either<string, JsonNode> TryGetProperty(this JsonObject? jsonObject, string propertyName) =>
        jsonObject is null
            ? "JSON object is null."
            : jsonObject.TryGetPropertyValue(propertyName, out var node)
                ? node is null
                    ? $"Property '{propertyName}' is null."
                    : Either<string, JsonNode>.Right(node)
                : $"Property '{propertyName}' is missing.";

    public static JsonObject GetJsonObjectProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.TryGetJsonObjectProperty(propertyName)
                  .IfLeftThrowJsonException();

    public static Either<string, JsonObject> TryGetJsonObjectProperty(this JsonObject? jsonObject, string propertyName) =>
        jsonObject.TryGetProperty(propertyName)
                  .Bind(node => node.TryAsJsonObject(propertyName));

    private static Either<string, JsonObject> TryAsJsonObject(this JsonNode node, string propertyName) =>
        node.TryAsJsonObject()
            .ToEither(() => $"Property '{propertyName}' is not a JSON object.");

    public static JsonArray GetJsonArrayProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.TryGetJsonArrayProperty(propertyName)
                  .IfLeftThrowJsonException();

    public static Either<string, JsonArray> TryGetJsonArrayProperty(this JsonObject? jsonObject, string propertyName) =>
        jsonObject.TryGetProperty(propertyName)
                  .Bind(node => node.TryAsJsonArray(propertyName));

    private static Either<string, JsonArray> TryAsJsonArray(this JsonNode node, string propertyName) =>
        node.TryAsJsonArray()
            .ToEither(() => $"Property '{propertyName}' is not a JSON array.");

    public static JsonValue GetJsonValueProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.TryGetJsonValueProperty(propertyName)
                  .IfLeftThrowJsonException();

    public static Either<string, JsonValue> TryGetJsonValueProperty(this JsonObject? jsonObject, string propertyName) =>
        jsonObject.TryGetProperty(propertyName)
                  .Bind(node => node.TryAsJsonValue(propertyName));

    private static Either<string, JsonValue> TryAsJsonValue(this JsonNode node, string propertyName) =>
        node.TryAsJsonValue()
            .ToEither(() => $"Property '{propertyName}' is not a JSON value.");

    public static string GetStringProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.TryGetStringProperty(propertyName)
                  .IfLeftThrowJsonException();

    public static Either<string, string> TryGetStringProperty(this JsonObject? jsonObject, string propertyName) =>
        jsonObject.TryGetProperty(propertyName)
                  .Bind(node => node.TryAsString(propertyName));

    private static Either<string, string> TryAsString(this JsonNode node, string propertyName) =>
        node.TryAsString()
            .ToEither(() => $"Property '{propertyName}' is not a string.");

    public static string GetNonEmptyOrWhiteSpaceStringProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.TryGetNonEmptyOrWhiteSpaceStringProperty(propertyName)
                  .IfLeftThrowJsonException();

    public static Either<string, string> TryGetNonEmptyOrWhiteSpaceStringProperty(this JsonObject? jsonObject, string propertyName) =>
        jsonObject.TryGetStringProperty(propertyName)
                  .Bind(value => string.IsNullOrWhiteSpace(value)
                                 ? Either<string, string>.Left($"Property '{propertyName}' is empty or whitespace.")
                                 : Either<string, string>.Right(value));

    public static Guid GetGuidProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.TryGetGuidProperty(propertyName)
                  .IfLeftThrowJsonException();

    public static Either<string, Guid> TryGetGuidProperty(this JsonObject? jsonObject, string propertyName) =>
        jsonObject.TryGetProperty(propertyName)
                  .Bind(node => node.TryAsGuid(propertyName));

    private static Either<string, Guid> TryAsGuid(this JsonNode node, string propertyName) =>
        node.TryAsGuid()
            .ToEither(() => $"Property '{propertyName}' is not a GUID.");

    public static Uri GetAbsoluteUriProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.TryGetAbsoluteUriProperty(propertyName)
                  .IfLeftThrowJsonException();

    public static Either<string, Uri> TryGetAbsoluteUriProperty(this JsonObject? jsonObject, string propertyName) =>
        jsonObject.TryGetProperty(propertyName)
                  .Bind(node => node.TryAsAbsoluteUri(propertyName));

    public static DateTimeOffset GetDateTimeOffsetProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.TryGetDateTimeOffsetProperty(propertyName)
                  .IfLeftThrowJsonException();

    public static Either<string, DateTimeOffset> TryGetDateTimeOffsetProperty(this JsonObject? jsonObject, string propertyName) =>
        jsonObject.TryGetProperty(propertyName)
                  .Bind(node => node.TryAsDateTimeOffset(propertyName));

    private static Either<string, DateTimeOffset> TryAsDateTimeOffset(this JsonNode node, string propertyName) =>
        node.TryAsDateTimeOffset()
            .ToEither(() => $"Property '{propertyName}' is not a valid DateTimeOffset.");

    public static DateTime GetDateTimeProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.TryGetDateTimeProperty(propertyName)
                  .IfLeftThrowJsonException();

    public static Either<string, DateTime> TryGetDateTimeProperty(this JsonObject? jsonObject, string propertyName) =>
        jsonObject.TryGetProperty(propertyName)
                  .Bind(node => node.TryAsDateTime(propertyName));

    private static Either<string, DateTime> TryAsDateTime(this JsonNode node, string propertyName) =>
        node.TryAsDateTime()
            .ToEither(() => $"Property '{propertyName}' is not a valid DateTime.");

    private static Either<string, Uri> TryAsAbsoluteUri(this JsonNode node, string propertyName) =>
        node.TryAsAbsoluteUri()
            .ToEither(() => $"Property '{propertyName}' is not an absolute URI.");

    public static int GetIntProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.TryGetIntProperty(propertyName)
                  .IfLeftThrowJsonException();

    public static Either<string, int> TryGetIntProperty(this JsonObject? jsonObject, string propertyName) =>
        jsonObject.TryGetProperty(propertyName)
                  .Bind(node => node.TryAsInt(propertyName));

    private static Either<string, int> TryAsInt(this JsonNode node, string propertyName) =>
        node.TryAsInt()
            .ToEither(() => $"Property '{propertyName}' is not an integer.");

    public static double GetDoubleProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.TryGetDoubleProperty(propertyName)
                  .IfLeftThrowJsonException();

    public static Either<string, double> TryGetDoubleProperty(this JsonObject? jsonObject, string propertyName) =>
        jsonObject.TryGetProperty(propertyName)
                  .Bind(node => node.TryAsDouble(propertyName));

    private static Either<string, double> TryAsDouble(this JsonNode node, string propertyName) =>
        node.TryAsDouble()
            .ToEither(() => $"Property '{propertyName}' is not a double.");

    public static bool GetBoolProperty(this JsonObject jsonObject, string propertyName) =>
        jsonObject.TryGetBoolProperty(propertyName)
                  .IfLeftThrowJsonException();

    public static Either<string, bool> TryGetBoolProperty(this JsonObject? jsonObject, string propertyName) =>
        jsonObject.TryGetProperty(propertyName)
                  .Bind(node => node.TryAsBool(propertyName));

    private static Either<string, bool> TryAsBool(this JsonNode node, string propertyName) =>
        node.TryAsBool()
            .ToEither(() => $"Property '{propertyName}' is not a boolean.");

    private static T IfLeftThrowJsonException<T>(this Either<string, T> either)
    {
        return either.IfLeft(left => throw new JsonException(left));
    }

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

    /// <summary>
    /// Sets <paramref name="jsonObject"/>[<paramref name="propertyName"/>] = <paramref name="jsonNode"/> if <paramref name="jsonNode"/> is not null.
    /// </summary>
    [return: NotNullIfNotNull(nameof(jsonObject))]
    public static JsonObject? SetPropertyIfNotNull(this JsonObject? jsonObject, string propertyName, JsonNode? jsonNode) =>
        jsonNode is null
            ? jsonObject
            : jsonObject.SetProperty(propertyName, jsonNode);

    /// <summary>
    /// Sets <paramref name="jsonObject"/>'s property <paramref name="propertyName"/> to the value of <paramref name="option"/> if <paramref name="option"/> is Some.
    /// </summary>
    [return: NotNullIfNotNull(nameof(jsonObject))]
    public static JsonObject? SetPropertyIfSome(this JsonObject? jsonObject, string propertyName, Option<JsonNode> option) =>
        jsonObject.SetPropertyIfNotNull(propertyName, option.ValueUnsafe());

    [return: NotNullIfNotNull(nameof(jsonObject))]
    public static JsonObject? RemoveProperty(this JsonObject? jsonObject, string propertyName)
    {
        if (jsonObject is null)
        {
            return null;
        }
        else
        {
            jsonObject.Remove(propertyName);
            return jsonObject;
        }
    }

    private static readonly JsonSerializerOptions serializerOptions = new() { PropertyNameCaseInsensitive = true };

    public static JsonObject Parse<T>(T obj) =>
        TryParse(obj)
            .IfNone(() => throw new JsonException($"Could not parse {typeof(T).Name} as a JSON object."));

    public static Option<JsonObject> TryParse<T>(T obj) =>
        JsonSerializer.SerializeToNode(obj, serializerOptions)
                      .TryAsJsonObject();
}

public static class JsonValueExtensions
{
    public static Option<string> TryAsString(this JsonValue? jsonValue) =>
        jsonValue is not null && jsonValue.TryGetValue<string>(out var value)
            ? value
            : Option<string>.None;

    public static Option<Guid> TryAsGuid(this JsonValue? jsonValue) =>
        jsonValue is not null && jsonValue.TryGetValue<Guid>(out var guid)
            ? guid
            : jsonValue.TryAsString()
                       .Bind(x => Guid.TryParse(x, out var guidFromString)
                                    ? guidFromString
                                    : Option<Guid>.None);

    public static Option<Uri> TryAsAbsoluteUri(this JsonValue? jsonValue) =>
        jsonValue.TryAsString()
                 .Bind(x => Uri.TryCreate(x, UriKind.Absolute, out var uri)
                                ? uri
                                : Option<Uri>.None);

    public static Option<DateTimeOffset> TryAsDateTimeOffset(this JsonValue? jsonValue) =>
        jsonValue is not null && jsonValue.TryGetValue<DateTimeOffset>(out var dateTimeOffset)
            ? dateTimeOffset
            : jsonValue.TryAsString()
                       .Bind(x => DateTimeOffset.TryParse(x, out var dateTime)
                                    ? dateTime
                                    : Option<DateTimeOffset>.None);

    public static Option<DateTime> TryAsDateTime(this JsonValue? jsonValue) =>
        jsonValue is not null && jsonValue.TryGetValue<DateTime>(out var dateTime)
            ? dateTime
            : jsonValue.TryAsString()
                       .Bind(x => DateTime.TryParse(x, out var dateTime)
                                    ? dateTime
                                    : Option<DateTime>.None);

    public static Option<int> TryAsInt(this JsonValue? jsonValue) =>
        jsonValue is not null && jsonValue.TryGetValue<int>(out var value)
            ? value
            : Option<int>.None;

    public static Option<double> TryAsDouble(this JsonValue? jsonValue) =>
    jsonValue is not null && jsonValue.TryGetValue<double>(out var value)
        ? value
        : Option<double>.None;

    public static Option<bool> TryAsBool(this JsonValue? jsonValue) =>
        jsonValue is not null && jsonValue.TryGetValue<bool>(out var value)
            ? value
            : Option<bool>.None;
}
