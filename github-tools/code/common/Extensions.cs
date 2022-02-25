namespace common;

public static class ObjectExtensions
{
    /// <summary>
    /// If <paramref name="value"/> is not null, apply <paramref name="mapper"/>; otherwise return null.
    /// </summary>
    public static U? Map<T, U>(this T? value, Func<T, U> mapper) where T : class where U : class
    {
        return value is null
            ? null
            : mapper(value);
    }

    /// <summary>
    /// If <paramref name="value"/> contains a value, apply <paramref name="mapper"/>; otherwise return default.
    /// </summary>
    public static U? Map<T, U>(this T? value, Func<T, U> mapper) where T : struct where U : struct
    {
        return value.HasValue
            ? mapper(value.Value)
            : default;
    }

    /// <summary>
    /// If <paramref name="value"/> is not null, apply <paramref name="mapper"/>; otherwise return null.
    /// Differs from <see cref="Map{T, U}(T?, Func{T, U})"/> in that this <paramref name="mapper"/> can return a null value.
    /// </summary>
    public static U? Bind<T, U>(this T? value, Func<T, U?> mapper) where T : class where U : class
    {
        return value is null
            ? null
            : mapper(value);
    }


    /// <summary>
    /// If <paramref name="value"/> contains a value, apply <paramref name="mapper"/>; otherwise return null.
    /// Differs from <see cref="Map{T, U}(T?, Func{T, U})"/> in that this <paramref name="mapper"/> can return a nullable value type.
    /// </summary>
    public static U? Bind<T, U>(this T? value, Func<T, U?> mapper) where T : struct where U : struct
    {
        return value.HasValue
            ? mapper(value.Value)
            : default;
    }

    public static T IfNullThrow<T>([NotNull] this T? value, string message) where T : class
    {
        return value ?? throw new InvalidOperationException(message);
    }

    public static T IfNullThrow<T>(this T? value, string message) where T : struct
    {
        return value ?? throw new InvalidOperationException(message);
    }

    public static T? ToNullIf<T>(this T value, Func<T, bool> predicate) where T : class
    {
        return predicate(value)
            ? null
            : value;
    }

    public static T IfNull<T>(this T? value, Func<T> nonNullAlternative) where T : class
    {
        return value ?? nonNullAlternative();
    }

    public static T IfNull<T>(this T? value, Func<T> nonNullAlternative) where T : struct
    {
        return value ?? nonNullAlternative();
    }
}

public static class TaskExtensions
{
    public static async Task<U> Map<T, U>(this Task<T> task, Func<T, U> mapper)
    {
        var t = await task;

        return mapper(t);
    }

    public static async Task<U> Bind<T, U>(this Task<T> task, Func<T, Task<U>> binder)
    {
        var t = await task;

        return await binder(t);
    }
}

public static class DirectoryInfoExtensions
{
    public static FileInfo GetFileInfo(this DirectoryInfo directory, FileName fileName)
    {
        var filePath = Path.Combine(directory.FullName, fileName);

        return new FileInfo(filePath);
    }

    public static DirectoryInfo GetParentDirectory(this DirectoryInfo directory)
    {
        return directory.Parent.IfNullThrow($"Directory {directory}'s parent is null.");
    }

    public static DirectoryInfo GetSubDirectory(this DirectoryInfo directory, DirectoryName directoryName)
    {
        var directoryPath = Path.Combine(directory.FullName, directoryName);

        return new DirectoryInfo(directoryPath);
    }

    [return: NotNullIfNotNull("directory")]
    public static DirectoryName? GetDirectoryName(this DirectoryInfo? directory)
    {
        return directory is null
            ? null
            : DirectoryName.From(directory.Name);
    }

    public static bool PathEquals([NotNullWhen(true)] this DirectoryInfo? directory, [NotNullWhen(true)] DirectoryInfo? otherDirectory)
    {
        return directory is not null
               && otherDirectory is not null
               && Path.GetRelativePath(directory.FullName, otherDirectory.FullName) == ".";
    }
}

public static class FileInfoExtensions
{
    public static async Task<Unit> OverwriteWithJson(this FileInfo file, JsonNode json, CancellationToken cancellationToken)
    {
        file.CreateDirectory();

        using var stream = file.Open(FileMode.Create);
        var options = new JsonSerializerOptions { WriteIndented = true };

        await JsonSerializer.SerializeAsync(stream, json, options, cancellationToken);

        return Unit.Default;
    }

    public static async Task<Unit> OverwriteWithText(this FileInfo file, string text, CancellationToken cancellationToken)
    {
        file.CreateDirectory();

        using var stream = file.Open(FileMode.Create);
        var textBytes = Encoding.UTF8.GetBytes(text);

        await stream.WriteAsync(textBytes, cancellationToken).AsTask();

        return Unit.Default;
    }

    public static async Task<Unit> OverwriteWithStream(this FileInfo file, Stream stream, CancellationToken cancellationToken)
    {
        file.CreateDirectory();

        using var fileStream = file.Open(FileMode.Create);

        await stream.CopyToAsync(fileStream, cancellationToken);
        
        return Unit.Default;
    }

    public static DirectoryInfo GetDirectoryInfo(this FileInfo file)
    {
        return file.Directory
               ?? throw new InvalidOperationException($"Cannot find directory associated with file path {file.FullName}.");
    }

    public static DirectoryName GetDirectoryName(this FileInfo file)
    {
        return DirectoryName.From(file.GetDirectoryInfo().Name);
    }

    public static async Task<JsonObject> ReadAsJsonObject(this FileInfo file, CancellationToken cancellationToken)
    {
        using var stream = file.OpenRead();

        var jsonObject = await JsonSerializer.DeserializeAsync<JsonObject>(stream, cancellationToken: cancellationToken)
                         ?? throw new InvalidOperationException($"Could not read JSON object from file {file}.");

        return jsonObject;
    }

    public static Task<string> ReadAsText(this FileInfo file, CancellationToken cancellationToken)
    {
        return File.ReadAllTextAsync(file.FullName, Encoding.UTF8, cancellationToken);
    }

    private static void CreateDirectory(this FileInfo file)
    {
        file.GetDirectoryInfo().Create();
    }
}

public static class JsonNodeExtensions
{
    public static JsonNode Clone(this JsonNode source)
    {
        return source.CloneUnsafe()
                     .IfNullThrow("Cloned node is null.");
    }

    /// <summary>
    /// Clones the node, potentially returning a null value.
    /// </summary>
    public static JsonNode? CloneUnsafe(this JsonNode? source)
    {
        return source.Map(node => JsonSerializer.SerializeToUtf8Bytes(node))
                     .Bind(bytes => JsonNode.Parse(bytes));
    }

    public static JsonObject? TryAsObject(this JsonNode? source)
    {
        return source.Bind(node => node as JsonObject);
    }

    public static JsonArray? TryAsArray(this JsonNode? source)
    {
        return source.Bind(node => node as JsonArray);
    }

    public static JsonValue? TryAsValue(this JsonNode? source)
    {
        return source.Bind(node => node as JsonValue);
    }

    public static string? TryAsStringValue(this JsonNode? source)
    {
        return source.TryAsValue()
                     .Bind(value => value.TryGetValue<string>());
    }

    public static string AsStringValue(this JsonNode source)
    {
        return source.AsValue()
                     .GetValue<string>();
    }

    public static JsonObject AddToJsonObject(this JsonNode? source, string propertyName, JsonObject target)
    {
        return target.AddNullableProperty(propertyName, source);
    }

    public static async Task<Unit> SerializeToStream(this JsonNode? source, Stream stream, CancellationToken cancellationToken)
    {
        await JsonSerializer.SerializeAsync(stream, source, cancellationToken: cancellationToken);

        stream.Position = 0;

        return Unit.Default;
    }

    private static T? TryGetValue<T>(this JsonValue jsonValue)
    {
        return jsonValue.TryGetValue<T>(out var value)
            ? value
            : default;
    }
}

public static class JsonArrayExtensions
{
    public static JsonArray Clone(this JsonArray source)
    {
        return source.Select(node => node.CloneUnsafe())
                     .ToJsonArray();
    }

    public static JsonArray ToJsonArray(this IEnumerable<JsonNode?> jsonNodes)
    {
        return new JsonArray(jsonNodes.ToArray());
    }
}

public static class JsonObjectExtensions
{
    public static JsonObject AddProperty(this JsonObject source, string propertyName, JsonNode value)
    {
        return source.AddNullableProperty(propertyName, value);
    }

    public static JsonObject AddStringProperty(this JsonObject source, string propertyName, string? value)
    {
        var jsonValue = JsonValue.Create(value);

        return source.AddNullableProperty(propertyName, jsonValue);
    }

    public static JsonObject AddNullableProperty(this JsonObject source, string propertyName, JsonNode? value)
    {
        var target = source.Clone();
        target[propertyName] = value;

        return target;
    }


    public static JsonNode? GetNullablePropertyValue(this JsonObject source, string propertyName)
    {
        return source.TryGetPropertyValue(propertyName, out var value)
            ? value
            : throw new InvalidOperationException($"Could not find property '{propertyName}' in JSON.");
    }

    public static JsonNode? TryGetPropertyValue(this JsonObject source, string propertyName)
    {
        return source.TryGetPropertyValue(propertyName, out var value)
            ? value
            : null;
    }

    public static JsonNode GetPropertyValue(this JsonObject source, string propertyName)
    {
        return source.GetNullablePropertyValue(propertyName)
               ?? throw new InvalidOperationException($"Property '{propertyName}' cannot be null.");
    }

    public static JsonObject? TryGetObjectPropertyValue(this JsonObject source, string propertyName)
    {
        return source.TryGetPropertyValue(propertyName)
                     .Bind(propertyValue => propertyValue.TryAsObject());
    }

    public static JsonObject GetObjectPropertyValue(this JsonObject source, string propertyName)
    {
        return source.GetPropertyValue(propertyName)
                     .AsObject();
    }

    public static string? TryGetNonEmptyStringPropertyValue(this JsonObject source, string propertyName)
    {
        return source.TryGetPropertyValue(propertyName)
                     .Bind(node => node.TryAsStringValue())
                     .Bind(value => value.ToNullIf(string.IsNullOrWhiteSpace));
    }

    public static string GetNonEmptyStringPropertyValue(this JsonObject source, string propertyName)
    {
        var stringValue = source.GetPropertyValue(propertyName)
                                .AsStringValue();

        return string.IsNullOrWhiteSpace(stringValue)
            ? throw new InvalidOperationException($"Property '{propertyName}' has an empty string value.")
            : stringValue;
    }

    public static JsonArray? TryGetArrayPropertyValue(this JsonObject source, string propertyName)
    {
        return source.TryGetPropertyValue(propertyName)
                     .Bind(propertyValue => propertyValue.TryAsArray());
    }

    public static IEnumerable<JsonObject>? TryGetObjectArrayPropertyValue(this JsonObject source, string propertyName)
    {
        return source.TryGetArrayPropertyValue(propertyName)
                     .Map(jsonArray => jsonArray.Choose(node => node.TryAsObject()));
    }

    public static IEnumerable<JsonObject> GetObjectArrayPropertyValue(this JsonObject source, string propertyName)
    {
        return source.GetArrayPropertyValue(propertyName)
                     .Choose(node => node.TryAsObject());
    }

    public static JsonArray GetArrayPropertyValue(this JsonObject source, string propertyName)
    {
        return source.GetPropertyValue(propertyName)
                     .AsArray();
    }

    public static JsonObject CopyPropertyFrom(this JsonObject target, string propertyName, JsonObject source)
    {
        var propertyValue = source.GetPropertyValue(propertyName)
                                  .Clone();

        return target.AddProperty(propertyName, propertyValue);
    }

    public static JsonObject CopyPropertyIfValueIsNonNullFrom(this JsonObject target, string propertyName, JsonObject source)
    {
        return source.TryGetPropertyValue(propertyName)
                     .Map(propertyValue => propertyValue.Clone())
                     .Map(clonedPropertyValue => target.AddProperty(propertyName, clonedPropertyValue))
                     .IfNull(() => target.Clone());
    }

    public static JsonObject CopyNullablePropertyFrom(this JsonObject target, string propertyName, JsonObject source)
    {
        var propertyValue = source.GetNullablePropertyValue(propertyName)
                                  .CloneUnsafe();

        return target.AddNullableProperty(propertyName, propertyValue);
    }

    public static JsonObject CopyObjectPropertyFrom(this JsonObject target, string propertyName, JsonObject source)
    {
        var propertyValue = source.GetObjectPropertyValue(propertyName)
                                  .Clone();

        return target.AddProperty(propertyName, propertyValue);
    }

    public static JsonObject CopyObjectPropertyFrom(this JsonObject target, string propertyName, JsonObject source, Func<JsonObject, JsonNode> formatPropertyValue)
    {
        var propertyValue = source.GetObjectPropertyValue(propertyName)
                                  .Clone();

        var formattedPropertyValue = formatPropertyValue(propertyValue);

        return target.AddProperty(propertyName, formattedPropertyValue);
    }

    public static JsonObject CopyObjectPropertyIfValueIsNonNullFrom(this JsonObject target, string propertyName, JsonObject source)
    {
        return source.TryGetObjectPropertyValue(propertyName)
                     .Map(propertyValue => propertyValue.Clone())
                     .Map(clonedPropertyValue => target.AddProperty(propertyName, clonedPropertyValue))
                     .IfNull(() => target.Clone());
    }

    public static JsonObject CopyObjectPropertyIfValueIsNonNullFrom(this JsonObject target, string propertyName, JsonObject source, Func<JsonObject, JsonNode> formatPropertyValue)
    {
        return source.TryGetObjectPropertyValue(propertyName)
                     .Map(propertyValue => propertyValue.Clone())
                     .Map(clonedPropertyValue => formatPropertyValue(clonedPropertyValue))
                     .Map(formattedPropertyValue => target.AddProperty(propertyName, formattedPropertyValue))
                     .IfNull(() => target.Clone());
    }

    public static JsonObject CopyObjectArrayPropertyFrom(this JsonObject target, string propertyName, JsonObject source)
    {
        var propertyValue = source.GetObjectArrayPropertyValue(propertyName)
                                  .Select(jsonObject => jsonObject.Clone())
                                  .ToJsonArray();

        return target.AddProperty(propertyName, propertyValue);
    }

    public static JsonObject CopyObjectArrayPropertyFrom(this JsonObject target, string propertyName, JsonObject source, Func<JsonObject, JsonNode> formatPropertyValue)
    {
        var propertyValue = source.GetObjectArrayPropertyValue(propertyName)
                                  .Select(jsonObject => jsonObject.Clone())
                                  .Select(formatPropertyValue)
                                  .ToJsonArray();

        return target.AddProperty(propertyName, propertyValue);
    }

    public static JsonObject CopyObjectArrayPropertyIfValueIsNonNullFrom(this JsonObject target, string propertyName, JsonObject source)
    {
        return source.TryGetObjectArrayPropertyValue(propertyName)
                     .Map(propertyValue => propertyValue.Select(jsonObject => jsonObject.Clone())
                                                        .ToJsonArray())
                     .Map(propertyValue => target.AddProperty(propertyName, propertyValue))
                     .IfNull(() => target.Clone());
    }

    public static JsonObject CopyObjectArrayPropertyIfValueIsNonNullFrom(this JsonObject target, string propertyName, JsonObject source, Func<JsonObject, JsonNode> formatPropertyValue)
    {
        return source.TryGetObjectArrayPropertyValue(propertyName)
                     .Map(propertyValue => propertyValue.Select(jsonObject => jsonObject.Clone())
                                                        .ToJsonArray())
                     .Map(propertyValue => target.AddProperty(propertyName, propertyValue))
                     .IfNull(() => target.Clone());
    }

    public static JsonObject Clone(this JsonObject source)
    {
        var dictionary = source.ToDictionary(kvp => kvp.Key,
                                             kvp => kvp.Value.CloneUnsafe());

        return new JsonObject(dictionary);
    }
}

public static class ArmClientExtensions
{
    public static Uri GetBaseUri(this ArmClient client)
    {
        return client.UseClientContext((baseUri, _tokenCredential, _options, _pipeline) => baseUri);
    }

    public static Task<JsonObject?> TryGetResourceJson(this ArmClient client, Uri uri, CancellationToken cancellationToken)
    {
        var pipeline = client.GetPipeline();
        var request = pipeline.CreateRequest(RequestMethod.Get, uri);

        return pipeline.SendRequestAsync(request, cancellationToken)
                       .AsTask()
                       .Bind(response => response.GetOptionalResourceJson(cancellationToken));
    }

    public static async Task<JsonObject> GetResource(this ArmClient client, Uri uri, CancellationToken cancellationToken)
    {
        return await TryGetResourceJson(client, uri, cancellationToken)
               ?? throw new InvalidOperationException($"Resource does not exist at URI {uri}.");
    }

    public static async Task<Unit> DeleteResource(this ArmClient client, Uri uri, CancellationToken cancellationToken)
    {
        var pipeline = client.GetPipeline();
        var request = pipeline.CreateRequest(RequestMethod.Delete, uri);

        await pipeline.SendRequestAsync(request, cancellationToken)
                       .AsTask()
                       .Map(response => response.ValidateSuccess());

        return Unit.Default;
    }

    public static async IAsyncEnumerable<JsonObject> GetResources(this ArmClient client, Uri uri, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Uri? nextLinkUri = uri;

        while (nextLinkUri is not null)
        {
            var resourcesJson = await GetResource(client, nextLinkUri, cancellationToken);
            (var resources, nextLinkUri) = getResources(resourcesJson);

            foreach (var resourceJson in resources)
            {
                yield return resourceJson;
            }
        }

        static (IEnumerable<JsonObject> Resources, Uri? NextLinkUri) getResources(JsonObject resourcesJson)
        {
            var resources = resourcesJson.GetObjectArrayPropertyValue("value");

            var nextLinkOption = resourcesJson.TryGetNonEmptyStringPropertyValue("nextLink")
                                              .Map(nextLinkUrl => new Uri(nextLinkUrl));

            return (resources, nextLinkOption);
        }
    }

    public static async Task<Unit> PutResource(this ArmClient client, Uri uri, Stream contentStream, CancellationToken cancellationToken)
    {
        var pipeline = client.GetPipeline();
        var request = pipeline.CreateRequest(RequestMethod.Put, uri, contentStream);

        var getRetryDuration = (int retryCount) =>
            Backoff.DecorrelatedJitterBackoffV2(TimeSpan.FromMilliseconds(500), retryCount, fastFirst: true)
                   .Last();

        var retryPolicy = Polly.Policy.HandleResult<Response>(response => response.Status == 404)
                               .WaitAndRetryAsync(5, getRetryDuration);

        await retryPolicy.ExecuteAsync(() => pipeline.SendRequestAsync(request, cancellationToken)
                                                     .AsTask()
                                                     .Map(response => response.ValidateSuccess()));

        return Unit.Default;
    }

    private static HttpPipeline GetPipeline(this ArmClient client)
    {
        return client.UseClientContext((_baseUri, _tokenCredential, _options, pipeline) => pipeline);
    }

    private static Request CreateRequest(this HttpPipeline pipeline, RequestMethod requestMethod, Uri uri)
    {
        var request = pipeline.CreateRequest();

        request.Method = requestMethod;
        request.Uri.Reset(uri);

        return request;
    }

    private static Request CreateRequest(this HttpPipeline pipeline, RequestMethod requestMethod, Uri uri, Stream contentStream)
    {
        var request = pipeline.CreateRequest();

        request.Method = requestMethod;
        request.Uri.Reset(uri);
        request.Content = RequestContent.Create(contentStream);
        request.Headers.Add("Content-type", "application/json");

        return request;
    }

    private static async Task<JsonObject?> GetOptionalResourceJson(this Response response, CancellationToken cancellationToken)
    {
        using var stream = response.GetOptionalResourceStream();

        return stream is null
            ? null
            : await JsonSerializer.DeserializeAsync<JsonObject>(stream, cancellationToken: cancellationToken)
                   ?? throw new InvalidOperationException("Failed to deserialize stream to JSON object.");
    }

    private static Stream? GetOptionalResourceStream(this Response response)
    {
        return response.Status switch
        {
            404 => null,
            _ => response.ValidateSuccess().ContentStream ?? throw new InvalidOperationException("Resource content stream is null.")
        };
    }

    private static Response ValidateSuccess(this Response response)
    {
        return response.IsSuccesful()
            ? response
            : throw new InvalidOperationException($"REST API call failed. Status code is {response.Status}, response content is '{response.Content}'.");
    }

    private static bool IsSuccesful(this Response response)
    {
        return response.Status is >= 200 and <= 299;
    }
}

public static class EnumerableExtensions
{
    /// <summary>
    /// Applies <paramref name="chooser"/> to the enumerable and filters out null results.
    /// </summary>
    public static IEnumerable<U> Choose<T, U>(this IEnumerable<T> enumerable, Func<T, U?> chooser)
    {
        return enumerable.Select(chooser)
                         .Where(x => x is not null)
                         .Select(x => x!);
    }

    /// <summary>
    /// Applies <paramref name="chooser"/> to the enumerable and filters out null results.
    /// </summary>
    public static IEnumerable<U> Choose<T, U>(this IEnumerable<T> enumerable, Func<T, U?> chooser) where U : struct
    {
        return enumerable.Select(chooser)
                         .Where(x => x.HasValue)
                         .Select(x => x!.Value);
    }

    public static async Task<Unit> ExecuteInParallel<T>(this IAsyncEnumerable<T> source, Func<T, Task<Unit>> action, CancellationToken cancellationToken)
    {
        await Parallel.ForEachAsync(source,
                                    cancellationToken,
                                    async (t, cancellationToken) => await action(t));

        return Unit.Default;
    }

    public static async Task<Unit> ExecuteInParallel<T>(this IEnumerable<T> source, Func<T, Task<Unit>> action, CancellationToken cancellationToken)
    {
        await Parallel.ForEachAsync(source,
                                    cancellationToken,
                                    async (t, cancellationToken) => await action(t));

        return Unit.Default;
    }

    public static async Task<Unit> ExecuteInParallel<T>(this IEnumerable<T> source, Func<T, CancellationToken, Task<Unit>> action, CancellationToken cancellationToken)
    {
        await Parallel.ForEachAsync(source,
                                    cancellationToken,
                                    async (t, cancellationToken) => await action(t, cancellationToken));

        return Unit.Default;
    }

    public static IEnumerable<T> Tap<T>(this IEnumerable<T> source, Action<T> action)
    {
        foreach (var t in source)
        {
            action(t);
            yield return t;
        }
    }

    /// <summary>
    /// Applies <paramref name="chooser"/> to the enumerable and returns the first result that is not null.
    /// If no result has a value, returns the default value
    /// </summary>
    /// <returns></returns>
    public static Task<U?> TryPick<T, U>(this IAsyncEnumerable<T> source, Func<T, U?> chooser, CancellationToken cancellationToken) where U : class
    {
        return source.Select(chooser)
                     .FirstOrDefaultAsync(u => u is not null, cancellationToken)
                     .AsTask();
    }

    /// <summary>
    /// Applies <paramref name="chooser"/> to the enumerable and returns the first result that has a value.
    /// If no result has a value, returns the default value
    /// </summary>
    /// <returns></returns>
    public static Task<U?> TryPick<T, U>(this IAsyncEnumerable<T> source, Func<T, U?> chooser, CancellationToken cancellationToken) where U : struct
    {
        return source.Select(chooser)
                     .FirstOrDefaultAsync(u => u.HasValue, cancellationToken)
                     .AsTask();
    }

    public static ILookup<TMappedKey, TValue> MapKeys<TKey, TMappedKey, TValue>(this ILookup<TKey, TValue> lookup, Func<TKey, TMappedKey> mapper)
    {
        return lookup.SelectMany(grouping => grouping.Select(value => (Key: mapper(grouping.Key), Value: value)))
                     .ToLookup(x => x.Key, x => x.Value);
    }

    public static ILookup<TKey, TValue> FilterKeys<TKey, TValue>(this ILookup<TKey, TValue> lookup, Func<TKey, bool> predicate)
    {
        return lookup.SelectMany(grouping => grouping.Choose(value => predicate(grouping.Key) ? (grouping.Key, value) : new (TKey Key, TValue Value)?()))
                     .ToLookup(x => x.Key, x => x.Value);
    }

    public static ILookup<TKey, TValue> RemoveNullKeys<TKey, TValue>(this ILookup<TKey?, TValue> lookup) where TKey : class
    {
        return lookup.FilterKeys(key => key is not null)!;
    }

    public static ILookup<TKey, TValue> RemoveNullKeys<TKey, TValue>(this ILookup<TKey?, TValue> lookup) where TKey : struct
    {
        return lookup.FilterKeys(key => key.HasValue).MapKeys(key => key!.Value);
    }

    public static IEnumerable<TValue> Lookup<TKey, TValue>(this ILookup<TKey, TValue> lookup, TKey key)
    {
        return lookup[key];
    }
}

public static class UriExtensions
{
    public static Uri AppendPath(this Uri uri, string pathSegment)
    {
        return Flurl.GeneratedExtensions.AppendPathSegment(uri, pathSegment)
                                        .ToUri();
    }

    public static Uri SetQueryParameter(this Uri uri, string parameterName, string parameterValue)
    {
        return Flurl.GeneratedExtensions.SetQueryParam(uri, parameterName, parameterValue)
                                        .ToUri();
    }
}

public static class StringExtensions
{
    public static JsonObject ToJsonObject([NotNull] this string? text)
    {
        return text.IfNullThrow("Cannot convert a null string to a JSON object.")
                   .Bind(input => JsonNode.Parse(input))
                   .IfNullThrow("Cannot parse input as a JSON node")
                   .Map(node => node.AsObject())
                   .IfNullThrow("JSON object cannot be null.");
    }
}