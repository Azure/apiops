using LanguageExt;
using LanguageExt.Common;
using LanguageExt.Traits;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Threading;

namespace common;

public static class JsonObjectModule
{
    public static JsonResult<JsonNode> GetProperty(this JsonObject? jsonObject, string propertyName) =>
        jsonObject switch
        {
            null => JsonResult.Fail<JsonNode>("JSON object is null."),
            _ => jsonObject.TryGetPropertyValue(propertyName, out var jsonNode)
                    ? jsonNode switch
                    {
                        null => JsonResult.Fail<JsonNode>($"Property '{propertyName}' is null."),
                        _ => JsonResult.Succeed(jsonNode)
                    }
                    : JsonResult.Fail<JsonNode>($"JSON object does not have a property named '{propertyName}'.")
        };

    public static Option<JsonNode> GetOptionalProperty(this JsonObject? jsonObject, string propertyName) =>
        jsonObject.GetProperty(propertyName)
                  .Match(Option<JsonNode>.Some,
                         _ => Option<JsonNode>.None);

    public static JsonResult<T> GetProperty<T>(this JsonObject? jsonObject, string propertyName, Func<JsonNode, JsonResult<T>> selector) =>
        jsonObject.GetProperty(propertyName)
                  .Bind(selector)
                  .AddPropertyNameToErrorMessage(propertyName);

    public static JsonResult<JsonObject> GetJsonObjectProperty(this JsonObject? jsonObject, string propertyName) =>
        jsonObject.GetProperty(propertyName,
                               jsonNode => jsonNode.AsJsonObject());

    private static JsonResult<T> AddPropertyNameToErrorMessage<T>(this JsonResult<T> result, string propertyName)
    {
        return result.ReplaceError(replaceError);

        JsonError replaceError(JsonError error) =>
            JsonError.From($"Property '{propertyName}' is invalid. {error.Message}");
    }

    public static JsonResult<JsonArray> GetJsonArrayProperty(this JsonObject? jsonObject, string propertyName) =>
        jsonObject.GetProperty(propertyName,
                               jsonNode => jsonNode.AsJsonArray());

    public static JsonResult<JsonValue> GetJsonValueProperty(this JsonObject? jsonObject, string propertyName) =>
        jsonObject.GetProperty(propertyName,
                               jsonNode => jsonNode.AsJsonValue());

    public static JsonResult<string> GetStringProperty(this JsonObject? jsonObject, string propertyName) =>
        jsonObject.GetProperty(propertyName,
                               jsonNode => jsonNode.AsJsonValue()
                                                   .Bind(jsonValue => jsonValue.AsString()));

    public static JsonResult<int> GetIntProperty(this JsonObject? jsonObject, string propertyName) =>
        jsonObject.GetProperty(propertyName,
                               jsonNode => jsonNode.AsJsonValue()
                                                   .Bind(jsonValue => jsonValue.AsInt()));

    public static JsonResult<bool> GetBoolProperty(this JsonObject? jsonObject, string propertyName) =>
        jsonObject.GetProperty(propertyName,
                               jsonNode => jsonNode.AsJsonValue()
                                                   .Bind(jsonValue => jsonValue.AsBool()));

    public static JsonResult<Guid> GetGuidProperty(this JsonObject? jsonObject, string propertyName) =>
        jsonObject.GetProperty(propertyName,
                               jsonNode => jsonNode.AsJsonValue()
                                                   .Bind(jsonValue => jsonValue.AsGuid()));

    public static JsonResult<Uri> GetAbsoluteUriProperty(this JsonObject? jsonObject, string propertyName) =>
        jsonObject.GetProperty(propertyName,
                               jsonNode => jsonNode.AsJsonValue()
                                                   .Bind(jsonValue => jsonValue.AsAbsoluteUri()));

    public static JsonObject SetProperty(this JsonObject jsonObject, string propertyName, JsonNode? propertyValue)
    {
        jsonObject[propertyName] = propertyValue;
        return jsonObject;
    }

    public static JsonResult<JsonObject> ToJsonObject(BinaryData? data, JsonSerializerOptions? options = default) =>
        JsonNodeModule.Deserialize<JsonObject>(data, options);
}

public static class JsonArrayModule
{
    public static JsonArray ToJsonArray(this IEnumerable<JsonNode?> nodes) =>
        new([.. nodes]);

    public static ValueTask<JsonArray> ToJsonArray(this IAsyncEnumerable<JsonNode?> nodes, CancellationToken cancellationToken) =>
        nodes.AggregateAsync(new JsonArray(),
                            (array, node) =>
                            {
                                array.Add(node);
                                return array;
                            },
                            cancellationToken);

    public static JsonResult<ImmutableArray<JsonObject>> GetJsonObjects(this JsonArray jsonArray) =>
        jsonArray.GetElements(jsonNode => jsonNode.AsJsonObject(),
                              index => JsonError.From($"Node at index {index} is not a JSON object."));

    public static JsonResult<ImmutableArray<T>> GetElements<T>(this JsonArray jsonArray, Func<JsonNode?, JsonResult<T>> selector, Func<int, JsonError> errorFromIndex)
    {
        return jsonArray.Select((node, index) => (node, index))
                        .AsIterable()
                        .Traverse(x => nodeToElement(x.node, x.index))
                        .As()
                        .Map(iterable => iterable.ToImmutableArray());

        JsonResult<T> nodeToElement(JsonNode? node, int index) =>
            selector(node)
                .ReplaceError(_ => errorFromIndex(index));
    }

    public static JsonResult<ImmutableArray<JsonArray>> GetJsonArrays(this JsonArray jsonArray) =>
        jsonArray.GetElements(jsonNode => jsonNode.AsJsonArray(),
                              index => JsonError.From($"Node at index {index} is not a JSON array."));

    public static JsonResult<ImmutableArray<JsonValue>> GetJsonValues(this JsonArray jsonArray) =>
        jsonArray.GetElements(jsonNode => jsonNode.AsJsonValue(),
                              index => JsonError.From($"Node at index {index} is not a JSON value."));
}

public static class JsonValueModule
{
    public static JsonResult<string> AsString(this JsonValue? jsonValue) =>
        jsonValue?.GetValueKind() switch
        {
            JsonValueKind.String => jsonValue.GetStringValue() switch
            {
                null => JsonResult.Fail<string>("JSON value has a null string."),
                var stringValue => JsonResult.Succeed(stringValue)
            },
            _ => JsonResult.Fail<string>("JSON value is not a string.")
        };

    private static string? GetStringValue(this JsonValue? jsonValue) =>
        jsonValue?.GetValue<object>().ToString();

    public static JsonResult<int> AsInt(this JsonValue? jsonValue)
    {
        var errorMessage = "JSON value is not an integer.";

        return jsonValue?.GetValueKind() switch
        {
            JsonValueKind.Number => int.TryParse(jsonValue.GetStringValue(), out var result)
                                    ? JsonResult.Succeed(result)
                                    : JsonResult.Fail<int>(errorMessage),
            _ => JsonResult.Fail<int>(errorMessage)
        };
    }

    public static JsonResult<bool> AsBool(this JsonValue? jsonValue) =>
        jsonValue?.GetValueKind() switch
        {
            JsonValueKind.True => JsonResult.Succeed(true),
            JsonValueKind.False => JsonResult.Succeed(false),
            _ => JsonResult.Fail<bool>("JSON value is not a boolean.")
        };

    public static JsonResult<Guid> AsGuid(this JsonValue? jsonValue)
    {
        var errorMessage = "JSON value is not a GUID.";

        return jsonValue.AsString()
                        .Bind(stringValue => Guid.TryParse(jsonValue.GetStringValue(), out var result)
                                            ? JsonResult.Succeed(result)
                                            : JsonResult.Fail<Guid>(errorMessage))
                        .ReplaceError(errorMessage);
    }

    public static JsonResult<Uri> AsAbsoluteUri(this JsonValue? jsonValue)
    {
        var errorMessage = "JSON value is not an absolute URI.";

        return jsonValue.AsString()
                 .Bind(stringValue => Uri.TryCreate(jsonValue.GetStringValue(), UriKind.Absolute, out var result)
                                        ? JsonResult.Succeed(result)
                                        : JsonResult.Fail<Uri>(errorMessage))
                 .ReplaceError(errorMessage);
    }
}

public static class JsonNodeModule
{
    public static JsonResult<JsonObject> AsJsonObject(this JsonNode? node) =>
        node switch
        {
            JsonObject jsonObject => JsonResult.Succeed(jsonObject),
            null => JsonResult.Fail<JsonObject>("JSON node is null."),
            _ => JsonResult.Fail<JsonObject>("JSON node is not a JSON object.")
        };

    public static JsonResult<JsonArray> AsJsonArray(this JsonNode? node) =>
        node switch
        {
            JsonArray jsonArray => JsonResult.Succeed(jsonArray),
            null => JsonResult.Fail<JsonArray>("JSON node is null."),
            _ => JsonResult.Fail<JsonArray>("JSON node is not a JSON array.")
        };

    public static JsonResult<JsonValue> AsJsonValue(this JsonNode? node) =>
        node switch
        {
            JsonValue jsonValue => JsonResult.Succeed(jsonValue),
            null => JsonResult.Fail<JsonValue>("JSON node is null."),
            _ => JsonResult.Fail<JsonValue>("JSON node is not a JSON value.")
        };

    public static JsonResult<T> Deserialize<T>(BinaryData? data, JsonSerializerOptions? options = default)
    {
        if (data is null)
        {
            return JsonResult.Fail<T>("Binary data is null.");
        }

        try
        {
            var jsonObject = JsonSerializer.Deserialize<T>(data, options ?? JsonSerializerOptions.Web);

            return jsonObject is null
                ? JsonResult.Fail<T>("Deserialization return a null result.")
                : JsonResult.Succeed(jsonObject);
        }
        catch (JsonException exception)
        {
            var jsonError = JsonError.From(exception);
            return JsonResult.Fail<T>(jsonError);
        }
    }
}

public sealed record JsonError : Error, Semigroup<JsonError>
{
    private readonly JsonException exception;

    private JsonError(JsonException exception) => this.exception = exception;

    private JsonError(string message) : this(new JsonException(message)) { }

    public override string Message => exception.Message;

    public override bool IsExceptional { get; }

    public override bool IsExpected { get; } = true;

    public override Exception ToException() => exception;

    public override ErrorException ToErrorException() => ErrorException.New(exception);

    public static JsonError From(string message) => new(message);

    public static JsonError From(JsonException exception) => new(exception);

    public override bool HasException<T>() => typeof(T).IsAssignableFrom(typeof(JsonException));

    public JsonError Combine(JsonError rhs) =>
        new(new JsonException("Multiple errors, see inner exception for details.",
                              new AggregateException(exception.InnerException switch
                              {
                                  AggregateException aggregateException => [.. aggregateException.InnerExceptions, rhs.exception],
                                  _ => [exception, rhs.exception]
                              })));

    public static JsonError operator +(JsonError lhs, JsonError rhs) =>
        lhs.Combine(rhs);
}

public class JsonResult :
    Monad<JsonResult>,
    Traversable<JsonResult>,
    Choice<JsonResult>
{
    public static JsonResult<T> Succeed<T>(T value) =>
        JsonResult<T>.Succeed(value);

    public static JsonResult<T> Fail<T>(JsonError error) =>
        JsonResult<T>.Fail(error);

    public static JsonResult<T> Fail<T>(string errorMessage) =>
        JsonResult<T>.Fail(JsonError.From(errorMessage));

    public static K<JsonResult, T> Pure<T>(T value) =>
        Succeed(value);

    public static K<JsonResult, T2> Bind<T1, T2>(K<JsonResult, T1> ma, Func<T1, K<JsonResult, T2>> f) =>
        ma.As()
          .Match(f, Fail<T2>);

    public static K<JsonResult, T2> Map<T1, T2>(Func<T1, T2> f, K<JsonResult, T1> ma) =>
        ma.As()
          .Match(t1 => Pure(f(t1)),
                 Fail<T2>);

    public static K<JsonResult, T2> Apply<T1, T2>(K<JsonResult, Func<T1, T2>> mf, K<JsonResult, T1> ma) =>
        mf.As()
          .Match(f => ma.Map(f),
                 error1 => ma.As()
                             .Match(t1 => Fail<T2>(error1),
                                    error2 => Fail<T2>(error1 + error2)));

    public static K<JsonResult, T> Choose<T>(K<JsonResult, T> fa, K<JsonResult, T> fb) =>
        fa.As()
          .Match(_ => fa,
                 _ => fb);

    public static K<TApplicative, K<JsonResult, T2>> Traverse<TApplicative, T1, T2>(Func<T1, K<TApplicative, T2>> f, K<JsonResult, T1> ta) where TApplicative : Applicative<TApplicative> =>
        (K<TApplicative, K<JsonResult, T2>>)
        ta.As()
          .Match(t1 => f(t1).Map(Succeed),
                 error => TApplicative.Pure(Fail<T2>(error)));

    public static S FoldWhile<A, S>(Func<A, Func<S, S>> f, Func<(S State, A Value), bool> predicate, S initialState, K<JsonResult, A> ta) =>
        ta.As()
          .Match(a => predicate((initialState, a))
                          ? f(a)(initialState)
                          : initialState,
                 _ => initialState);

    public static S FoldBackWhile<A, S>(Func<S, Func<A, S>> f, Func<(S State, A Value), bool> predicate, S initialState, K<JsonResult, A> ta) =>
        ta.As()
          .Match(a => predicate((initialState, a))
                          ? f(initialState)(a)
                          : initialState,
                 _ => initialState);
}

public class JsonResult<T> :
    IEquatable<JsonResult<T>>,
    K<JsonResult, T>
{
    private readonly Either<JsonError, T> value;

    private JsonResult(Either<JsonError, T> value) => this.value = value;

    public T2 Match<T2>(Func<T, T2> Succ, Func<JsonError, T2> Fail) =>
        value.Match(Fail, Succ);

    public Unit Match(Action<T> Succ, Action<JsonError> Fail) =>
        value.Match(Fail, Succ);

    internal static JsonResult<T> Succeed(T value) =>
        new(value);

    internal static JsonResult<T> Fail(JsonError error) =>
        new(error);

    public override bool Equals(object? obj) =>
        obj is JsonResult<T> result && Equals(result);

    public override int GetHashCode() =>
        value.GetHashCode();

    public bool Equals(JsonResult<T>? other) =>
        other is not null
        && this.Match(t => other.Match(t2 => t?.Equals(t2) ?? false,
                                       _ => false),
                      error => other.Match(_ => false,
                                           error2 => error.Equals(error2)));
}
public static class JsonResultExtensions
{
    public static JsonResult<T> As<T>(this K<JsonResult, T> k) =>
        (JsonResult<T>)k;

    public static JsonResult<T2> Map<T1, T2>(this JsonResult<T1> result, Func<T1, T2> f) =>
        result.Match(t1 => JsonResult.Succeed(f(t1)),
                     JsonResult<T2>.Fail);

    public static JsonResult<T2> Bind<T1, T2>(this JsonResult<T1> result, Func<T1, JsonResult<T2>> f) =>
        result.Match(f, JsonResult<T2>.Fail);

    public static JsonResult<T> ReplaceError<T>(this JsonResult<T> result, string newErrorMessage) =>
        result.ReplaceError(JsonError.From(newErrorMessage));

    public static JsonResult<T> ReplaceError<T>(this JsonResult<T> result, JsonError newError) =>
        result.ReplaceError(_ => newError);

    public static JsonResult<T> ReplaceError<T>(this JsonResult<T> result, Func<JsonError, JsonError> f) =>
        result.Match(_ => result,
                     error => JsonResult.Fail<T>(f(error)));

    public static T IfFail<T>(this JsonResult<T> result, Func<JsonError, T> f) =>
        result.Match(t => t, f);

    public static T? DefaultIfFail<T>(this JsonResult<T> result) =>
        result.Match<T?>(t => t, _ => default);

    public static T ThrowIfFail<T>(this JsonResult<T> result) =>
        result.IfFail(error => throw error.ToException());

    public static Fin<T> ToFin<T>(this JsonResult<T> result) =>
        result.Match(Fin<T>.Succ, Fin<T>.Fail);

    // Enable LINQ query syntax
    public static JsonResult<T2> Select<T1, T2>(this JsonResult<T1> result, Func<T1, T2> f) =>
        result.Map(f);

    public static JsonResult<T2> SelectMany<T1, T2>(this JsonResult<T1> result, Func<T1, JsonResult<T2>> f) =>
        result.Bind(f);

    public static JsonResult<T3> SelectMany<T1, T2, T3>(this JsonResult<T1> result, Func<T1, JsonResult<T2>> bind, Func<T1, T2, T3> project) =>
        result.Bind(t1 => bind(t1).Map(t2 => project(t1, t2)));
}