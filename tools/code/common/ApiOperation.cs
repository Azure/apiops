using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace common;

public sealed record ApiOperationName : NonEmptyString
{
    private ApiOperationName(string value) : base(value)
    {
    }

    public static ApiOperationName From(string value) => new(value);
}

public sealed record ApiOperationDisplayName : NonEmptyString
{
    private ApiOperationDisplayName(string value) : base(value)
    {
    }

    public static ApiOperationDisplayName From(string value) => new(value);
}

public sealed record ApiOperationUri : UriRecord
{
    public ApiOperationUri(Uri value) : base(value)
    {
    }

    public static ApiOperationUri From(ApiUri apiUri, ApiOperationName operationName) =>
        new(UriExtensions.AppendPath(apiUri, "operations").AppendPath(operationName));
}

public sealed record ApiOperationsDirectory : DirectoryRecord
{
    private static readonly string name = "operations";

    private ApiOperationsDirectory(RecordPath path) : base(path)
    {
    }

    public static ApiOperationsDirectory From(ApisDirectory apisDirectory, ApiDisplayName apiDisplayName) =>
        new(apisDirectory.Path.Append(apiDisplayName));

    public static ApiOperationsDirectory? TryFrom(ServiceDirectory serviceDirectory, DirectoryInfo? directory) =>
        name.Equals(directory?.Name) && serviceDirectory.Path.PathEquals(directory.Parent?.Parent?.Parent?.FullName)
        ? new(RecordPath.From(directory.FullName))
        : null;
}

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
        JsonSerializer.Deserialize<ApiOperation>(jsonObject) ?? throw new InvalidOperationException("Could not deserialize object.");

    public static Uri GetListByApiUri(ApiUri apiUri) => UriExtensions.AppendPath(apiUri, "operations");
}