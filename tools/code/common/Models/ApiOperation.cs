using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace common.Models;

public sealed record ApiOperation([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("properties")] ApiOperation.OperationContractProperties Properties)
{
    public record OperationContractProperties([property: JsonPropertyName("displayName")] string DisplayName,
                                              [property: JsonPropertyName("method")] string Method,
                                              [property: JsonPropertyName("urlTemplate")] string UrlTemplate)
    {
    }

    public JsonObject ToJsonObject() =>
        JsonSerializer.SerializeToNode(this)?.AsObject() ?? throw new InvalidOperationException("Could not serialize object.");

    public static ApiOperation FromJsonObject(JsonObject jsonObject) =>
        jsonObject.Deserialize<ApiOperation>() ?? throw new InvalidOperationException("Could not deserialize object.");
}