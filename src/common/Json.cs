using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;

namespace common;

/// <summary>
/// Provides extension methods for working with <see cref="JsonNode"/> instances in a functional style.
/// </summary>
public static class JsonNodeModule
{
    /// <summary>
    /// Safely casts a <see cref="JsonNode"/> to a <see cref="JsonObject"/>.
    /// </summary>
    /// <param name="node">The <see cref="JsonNode"/> to cast.</param>
    /// <returns>Success with the <see cref="JsonObject"/> if cast succeeds, otherwise error details.</returns>
    public static Result<JsonObject> AsJsonObject(this JsonNode? node) =>
        node switch
        {
            JsonObject jsonObject => Result.Success(jsonObject),
            null => Error.From("JSON node is null."),
            _ => Error.From("JSON node is not a JSON object.")
        };

    /// <summary>
    /// Safely casts a <see cref="JsonNode"/> to a <see cref="JsonArray"/>.
    /// </summary>
    /// <param name="node">The <see cref="JsonNode"/> to cast.</param>
    /// <returns>Success with the <see cref="JsonArray"/> if cast succeeds, otherwise error details.</returns>
    public static Result<JsonArray> AsJsonArray(this JsonNode? node) =>
        node switch
        {
            JsonArray jsonArray => Result.Success(jsonArray),
            null => Error.From("JSON node is null."),
            _ => Error.From("JSON node is not a JSON array.")
        };

    /// <summary>
    /// Safely casts a <see cref="JsonNode"/> to a <see cref="JsonValue"/>.
    /// </summary>
    /// <param name="node">The <see cref="JsonNode"/> to cast.</param>
    /// <returns>Success with the <see cref="JsonValue"/> if cast succeeds, otherwise error details.</returns>
    public static Result<JsonValue> AsJsonValue(this JsonNode? node) =>
        node switch
        {
            JsonValue jsonValue => Result.Success(jsonValue),
            null => Error.From("JSON node is null."),
            _ => Error.From("JSON node is not a JSON value.")
        };

    /// <summary>
    /// Parses JSON data from binary data into a <see cref="JsonNode"/>.
    /// </summary>
    /// <param name="data">The <see cref="BinaryData"/> containing JSON.</param>
    /// <param name="options">Options to control parsing behavior.</param>
    /// <returns>Success with the parsed <see cref="JsonNode"/>, or error if parsing fails.</returns>
    public static Result<JsonNode> From(BinaryData? data, JsonNodeOptions? options = default)
    {
        try
        {
            return data switch
            {
                null => Result.Error<JsonNode>("Binary data is null."),
                { IsEmpty: true } => Result.Error<JsonNode>("Binary data is empty."),
                _ => JsonNode.Parse(data, options) switch
                {
                    null => Error.From("Deserialization returned a null result."),
                    var node => node
                }
            };
        }
        catch (JsonException exception)
        {
            return Error.From(exception);
        }
    }

    /// <summary>
    /// Serializes an object to a <see cref="JsonNode"/>.
    /// </summary>
    /// <typeparam name="T">The type of the object to serialize.</typeparam>
    /// <param name="data">The object to serialize.</param>
    /// <param name="options">Serializer options.</param>
    /// <returns>Success with the serialized <see cref="JsonNode"/>, or error if serialization fails.</returns>
    public static Result<JsonNode> From<T>(T? data, JsonSerializerOptions? options = default)
    {
        try
        {
            return JsonSerializer.SerializeToNode(data, options) switch
            {
                null => Error.From("Serialization returned a null result."),
                var jsonNode => jsonNode
            };
        }
        catch (JsonException exception)
        {
            return Error.From(exception);
        }
    }

    /// <summary>
    /// Deserializes a <see cref="JsonNode"/> into a strongly-typed object.
    /// </summary>
    /// <typeparam name="T">The type to deserialize into.</typeparam>
    /// <param name="node">The <see cref="JsonNode"/> to deserialize.</param>
    /// <param name="options">Serializer options. Defaults to <see cref="JsonSerializerOptions.Web"/> if null.</param>
    /// <returns>Success with the deserialized object, or error if deserialization fails.</returns>
    public static Result<T> To<T>(JsonNode? node, JsonSerializerOptions? options = default)
    {
        if (node is null)
        {
            return Error.From("JSON is null.");
        }

        try
        {
            return node.Deserialize<T>(options ?? JsonSerializerOptions.Web) switch
            {
                null => Error.From("Deserialization returned a null result."),
                var t => t
            };
        }
        catch (JsonException exception)
        {
            return Error.From(exception);
        }
    }

    /// <summary>
    /// Deserializes JSON binary data into a strongly-typed object.
    /// </summary>
    /// <typeparam name="T">The type to deserialize into.</typeparam>
    /// <param name="data">The <see cref="BinaryData"/> containing JSON.</param>
    /// <param name="options">Serializer options. Defaults to <see cref="JsonSerializerOptions.Web"/> if null.</param>
    /// <returns>Success with the deserialized object, or error if deserialization fails.</returns>
    public static Result<T> Deserialize<T>(BinaryData? data, JsonSerializerOptions? options = default)
    {
        if (data is null)
        {
            return Error.From("Binary data is null.");
        }

        try
        {
            var jsonObject = JsonSerializer.Deserialize<T>(data, options ?? JsonSerializerOptions.Web);

            return jsonObject is null
                    ? Error.From("Deserialization returned a null result.")
                    : jsonObject;
        }
        catch (JsonException exception)
        {
            return Error.From(exception);
        }
    }
}

/// <summary>
/// Provides extension methods for working with <see cref="JsonValue"/> instances in a functional style.
/// </summary>
public static class JsonValueModule
{
    /// <summary>
    /// Safely converts a <see cref="JsonValue"/> to a string.
    /// </summary>
    /// <param name="jsonValue">The <see cref="JsonValue"/> to convert.</param>
    /// <returns>Success with the <see cref="string"/> value if <see cref="JsonValue"/> contains a string, otherwise error details.</returns>
    public static Result<string> AsString(this JsonValue? jsonValue) =>
        jsonValue?.GetValueKind() switch
        {
            JsonValueKind.String => jsonValue.GetStringValue() switch
            {
                null => Error.From("JSON value has a null string."),
                var stringValue => Result.Success(stringValue),
            },
            _ => Error.From("JSON value is not a string.")
        };

    private static string? GetStringValue(this JsonValue? jsonValue) =>
        jsonValue?.GetValue<object>()?.ToString();

    /// <summary>
    /// Safely converts a <see cref="JsonValue"/> to an absolute URI.
    /// </summary>
    /// <param name="jsonValue">The <see cref="JsonValue"/> to convert.</param>
    /// <returns>Success with the <see cref="Uri"/> value if <see cref="JsonValue"/> contains a valid absolute URI string, otherwise error details.</returns>
    public static Result<Uri> AsAbsoluteUri(this JsonValue? jsonValue)
    {
        var errorMessage = "JSON value is not an absolute URI.";

        return jsonValue.AsString()
                        .Bind(stringValue => Uri.TryCreate(stringValue, UriKind.Absolute, out var result)
                                                ? Result.Success(result)
                                                : Error.From(errorMessage))
                        .MapError(_ => errorMessage);
    }
}

/// <summary>
/// Provides extension methods for working with <see cref="JsonArray"/> instances in a functional style.
/// </summary>
public static class JsonArrayModule
{
    /// <summary>
    /// Converts an enumerable of <see cref="JsonNode"/> instances to a <see cref="JsonArray"/>.
    /// </summary>
    /// <param name="nodes">The enumerable to convert.</param>
    /// <returns>A <see cref="JsonArray"/> containing all nodes.</returns>
    public static JsonArray ToJsonArray(this IEnumerable<JsonNode?> nodes) =>
        new([.. nodes]);

    /// <summary>
    /// Extracts all <see cref="JsonObject"/> elements from a <see cref="JsonArray"/>.
    /// </summary>
    /// <param name="jsonArray">The <see cref="JsonArray"/> to process.</param>
    /// <returns>Success with <see cref="JsonObject"/>s if all elements are objects, otherwise error details.</returns>
    public static Result<ImmutableArray<JsonObject>> GetJsonObjects(this JsonArray jsonArray) =>
        jsonArray.GetElements(jsonNode => jsonNode.AsJsonObject(),
                              index => Error.From($"Node at index {index} is not a JSON object."));

    /// <summary>
    /// Extracts elements from a <see cref="JsonArray"/> using a selector function, collecting successes or aggregating errors.
    /// </summary>
    /// <typeparam name="T">The type of elements to extract.</typeparam>
    /// <param name="jsonArray">The <see cref="JsonArray"/> to process.</param>
    /// <param name="selector">Function that converts a <see cref="JsonNode"/> to the desired type.</param>
    /// <param name="errorFromIndex">Function that creates an error message for a failed conversion at a given index.</param>
    /// <returns>Success with extracted elements if all conversions succeed, otherwise aggregated error details.</returns>
    public static Result<ImmutableArray<T>> GetElements<T>(this JsonArray jsonArray, Func<JsonNode?, Result<T>> selector, Func<int, Error> errorFromIndex)
    {
        return jsonArray.Select((node, index) => (node, index))
                        .Traverse(x => nodeToElement(x.node, x.index), CancellationToken.None);

        Result<T> nodeToElement(JsonNode? node, int index) =>
            selector(node)
                .MapError(error => errorFromIndex(index));
    }
}

/// <summary>
/// Provides extension methods for working with <see cref="JsonObject"/> instances in a functional style.
/// </summary>
public static class JsonObjectModule
{
    /// <summary>
    /// Safely retrieves a property value from a <see cref="JsonObject"/>.
    /// </summary>
    /// <param name="jsonObject">The <see cref="JsonObject"/> to query.</param>
    /// <param name="propertyName">The property name.</param>
    /// <returns>Success with the <see cref="JsonNode"/> property value if found, otherwise error details.</returns>
    public static Result<JsonNode> GetProperty(this JsonObject? jsonObject, string propertyName) =>
        jsonObject switch
        {
            null => Result.Error<JsonNode>("JSON object is null."),
            _ => jsonObject.TryGetPropertyValue(propertyName, out var jsonNode)
                    ? jsonNode switch
                    {
                        null => Result.Error<JsonNode>($"Property '{propertyName}' is null."),
                        _ => jsonNode
                    }
                    : Error.From($"JSON object does not have a property named '{propertyName}'.")
        };

    /// <summary>
    /// Safely retrieves and transforms a property value from a <see cref="JsonObject"/> using a selector function.
    /// </summary>
    /// <typeparam name="T">The type to transform into.</typeparam>
    /// <param name="jsonObject">The <see cref="JsonObject"/> to query.</param>
    /// <param name="propertyName">The property name.</param>
    /// <param name="selector">Function that transforms the <see cref="JsonNode"/> into the desired type.</param>
    /// <returns>Success with the transformed value if property exists and transformation succeeds, otherwise error with property context.</returns>
    public static Result<T> GetProperty<T>(this JsonObject? jsonObject, string propertyName, Func<JsonNode, Result<T>> selector) =>
        jsonObject.GetProperty(propertyName)
                  .Bind(selector)
                  .AddPropertyNameToErrorMessage(propertyName);

    /// <summary>
    /// Safely retrieves a <see cref="JsonObject"/> property from a <see cref="JsonObject"/>.
    /// </summary>
    /// <param name="jsonObject">The <see cref="JsonObject"/> to query.</param>
    /// <param name="propertyName">The property name.</param>
    /// <returns>Success with the <see cref="JsonObject"/> if found and is an object, otherwise error details.</returns>
    public static Result<JsonObject> GetJsonObjectProperty(this JsonObject? jsonObject, string propertyName) =>
        jsonObject.GetProperty(propertyName,
                               jsonNode => jsonNode.AsJsonObject());

    /// <summary>
    /// Adds property name context to error messages for better diagnostics.
    /// </summary>
    /// <typeparam name="T">The result value type.</typeparam>
    /// <param name="result">The result to enhance.</param>
    /// <param name="propertyName">The property name to include in errors.</param>
    /// <returns>The original result if successful, otherwise error with enhanced context.</returns>
    private static Result<T> AddPropertyNameToErrorMessage<T>(this Result<T> result, string propertyName)
    {
        return result.MapError(replaceError);

        Error replaceError(Error error) =>
            Error.From($"Property '{propertyName}' is invalid. {error}");
    }

    /// <summary>
    /// Safely retrieves a <see cref="JsonArray"/> property from a <see cref="JsonObject"/>.
    /// </summary>
    /// <param name="jsonObject">The <see cref="JsonObject"/> to query.</param>
    /// <param name="propertyName">The property name.</param>
    /// <returns>Success with the <see cref="JsonArray"/> if found and is an array, otherwise error details.</returns>
    public static Result<JsonArray> GetJsonArrayProperty(this JsonObject? jsonObject, string propertyName) =>
        jsonObject.GetProperty(propertyName,
                               jsonNode => jsonNode.AsJsonArray());

    /// <summary>
    /// Safely retrieves a string property from a <see cref="JsonObject"/>.
    /// </summary>
    /// <param name="jsonObject">The <see cref="JsonObject"/> to query.</param>
    /// <param name="propertyName">The property name.</param>
    /// <returns>Success with the <see cref="string"/> value if found and is a string, otherwise error details.</returns>
    public static Result<string> GetStringProperty(this JsonObject? jsonObject, string propertyName) =>
        jsonObject.GetProperty(propertyName,
                               jsonNode => jsonNode.AsJsonValue()
                                                   .Bind(jsonValue => jsonValue.AsString()));

    /// <summary>
    /// Safely retrieves an absolute URI property from a <see cref="JsonObject"/>.
    /// </summary>
    /// <param name="jsonObject">The <see cref="JsonObject"/> to query.</param>
    /// <param name="propertyName">The property name.</param>
    /// <returns>Success with the <see cref="Uri"/> value if found and is a valid absolute URI string, otherwise error details.</returns>
    public static Result<Uri> GetAbsoluteUriProperty(this JsonObject? jsonObject, string propertyName) =>
        jsonObject.GetProperty(propertyName,
                               jsonNode => jsonNode.AsJsonValue()
                                                   .Bind(jsonValue => jsonValue.AsAbsoluteUri()));

    /// <summary>
    /// Sets a property in the JSON object.
    /// </summary>
    /// <param name="jsonObject">The <see cref="JsonObject"/> to modify.</param>
    /// <param name="propertyName">The property name.</param>
    /// <param name="propertyValue">The property value.</param>
    /// <param name="mutateOriginal">If true, modifies the original; if false, creates a deep copy first.</param>
    /// <returns>A <see cref="JsonObject"/> with the property set.</returns>
    public static JsonObject SetProperty(this JsonObject jsonObject, string propertyName, JsonNode? propertyValue, bool mutateOriginal = false)
    {
        var newJson = mutateOriginal
                        ? jsonObject
                        : jsonObject.DeepClone().AsObject();

        newJson[propertyName] = propertyValue;

        return newJson;
    }

    /// <summary>
    /// Removes a property from the JSON object.
    /// </summary>
    /// <param name="jsonObject">The <see cref="JsonObject"/> to modify.</param>
    /// <param name="propertyName">The property name.</param>
    /// <param name="mutateOriginal">If true, modifies the original; if false, creates a deep copy first.</param>
    /// <returns>A <see cref="JsonObject"/> with the property removed.</returns>
    public static JsonObject RemoveProperty(this JsonObject jsonObject, string propertyName, bool mutateOriginal = false)
    {
        var newJson = mutateOriginal
                        ? jsonObject
                        : jsonObject.DeepClone().AsObject();

        newJson.Remove(propertyName);

        return newJson;
    }

    /// <summary>
    /// Merges another <see cref="JsonObject"/> into the current one.
    /// </summary>
    /// <param name="original">The original <see cref="JsonObject"/> to merge into.</param>
    /// <param name="other">The <see cref="JsonObject"/> to merge from. If null, returns the original object.</param>
    /// <param name="mutateOriginal">If true, modifies the original; if false, creates a deep copy first.</param>
    /// <returns>A <see cref="JsonObject"/> with properties from both objects, with <paramref name="other"/> taking precedence for duplicate keys.</returns>
    public static JsonObject MergeWith(this JsonObject original, JsonObject? other, bool mutateOriginal = false)
    {
        if (other is null || other.Count == 0)
        {
            return original;
        }

        var mergedJson = mutateOriginal
                            ? original
                            : original.DeepClone().AsObject();

        foreach (var kvp in other)
        {
            var (otherKey, otherValue) = kvp;
            var updatedValue = mergedJson.GetProperty(otherKey)
                                         .Map(existingValue => existingValue is JsonObject existingJsonObject && otherValue is JsonObject otherJsonObject
                                                                ? existingJsonObject.MergeWith(otherJsonObject, mutateOriginal)
                                                                : otherValue)
                                         .IfError(_ => otherValue);
            mergedJson[otherKey] = updatedValue?.DeepClone();
        }

        return mergedJson;
    }

    /// <summary>
    /// Deserializes binary data into a <see cref="JsonObject"/>.
    /// </summary>
    /// <param name="data">The <see cref="BinaryData"/> containing JSON.</param>
    /// <param name="options">Serializer options. Defaults to <see cref="JsonSerializerOptions.Web"/> if null.</param>
    /// <returns>Success with the deserialized <see cref="JsonObject"/>, or error if deserialization fails.</returns>
    public static Result<JsonObject> From(BinaryData? data, JsonSerializerOptions? options = default) =>
        JsonNodeModule.Deserialize<JsonObject>(data, options);

    /// <summary>
    /// Deserializes binary data into a <see cref="JsonObject"/>.
    /// </summary>
    /// <param name="data">The <see cref="BinaryData"/> containing JSON.</param>
    /// <param name="options">Serializer options. Defaults to <see cref="JsonSerializerOptions.Web"/> if null.</param>
    /// <returns>Success with the deserialized <see cref="JsonObject"/>, or error if deserialization fails.</returns>
    public static Result<JsonObject> From<T>(T data, JsonSerializerOptions? options = default) =>
        from node in JsonNodeModule.From(data, options)
        from jsonObject in node.AsJsonObject()
        select jsonObject;
}

public sealed class OptionJsonConverter : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType
        && typeToConvert.GetGenericTypeDefinition() == typeof(Option<>);

    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)
        Activator.CreateInstance(typeof(OptionConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]),
                                 BindingFlags.Instance | BindingFlags.Public,
                                 binder: null,
                                 args: null,
                                 culture: null)!;

#pragma warning disable CA1812 // Avoid uninstantiated internal classes
    private sealed class OptionConverter<T> : JsonConverter<Option<T>>
#pragma warning restore CA1812 // Avoid uninstantiated internal classes
    {
        public OptionConverter() { }

        public override Option<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Null
                ? Option.None
                : JsonSerializer.Deserialize<T>(ref reader, options) switch
                {
                    null => Option.None,
                    var value => Option.Some(value)
                };

        public override void Write(Utf8JsonWriter writer, Option<T> value, JsonSerializerOptions options) =>
            value.Match(some => JsonSerializer.Serialize(writer, some, options),
                        writer.WriteNullValue);
    }
}