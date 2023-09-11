using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace publisher;

internal static class JsonExtensions
{
    [return: NotNullIfNotNull(nameof(source))]
    [return: NotNullIfNotNull(nameof(destination))]
    public static JsonObject? Merge(this JsonObject? destination, JsonObject? source)
    {
        switch (source, destination)
        {
            case (null, null): return null;
            case (_, null): return source;
            case (null, _): return destination;
            case (_, _):
                var destinationJObject = JObject.Parse(JsonSerializer.Serialize(destination));
                var sourceJObject = JObject.Parse(JsonSerializer.Serialize(source));

                destinationJObject.Merge(sourceJObject, new JsonMergeSettings
                {
                    MergeArrayHandling = MergeArrayHandling.Union,
                    MergeNullValueHandling = MergeNullValueHandling.Merge,
                    PropertyNameComparison = StringComparison.OrdinalIgnoreCase
                });

                return JsonSerializer.Deserialize<JsonObject>(destinationJObject.ToString())
                        ?? throw new InvalidOperationException("Merged JSON object cannot be null.");
        }
    }
}
