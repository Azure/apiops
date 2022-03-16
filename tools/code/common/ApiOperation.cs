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

    public ApiDirectory ApiDirectory { get; }

    private ApiOperationsDirectory(ApiDirectory apiDirectory) : base(apiDirectory.Path.Append(name))
    {
        ApiDirectory = apiDirectory;
    }

    public static ApiOperationsDirectory From(ApiDirectory apiDirectory) => new(apiDirectory);

    public static ApiOperationsDirectory? TryFrom(ServiceDirectory serviceDirectory, DirectoryInfo? directory)
    {
        var parentDirectory = directory?.Parent;
        if (parentDirectory is not null)
        {
            var apiDirectory = ApiDirectory.TryFrom(serviceDirectory, parentDirectory);

            return apiDirectory is null ? null : From(apiDirectory);
        }
        else
        {
            return null;
        }
    }
}

public sealed record ApiOperationDirectory : DirectoryRecord
{
    public ApiOperationsDirectory ApiOperationsDirectory { get; }
    public ApiOperationDisplayName ApiOperationDisplayName { get; }

    private ApiOperationDirectory(ApiOperationsDirectory apiOperationsDirectory, ApiOperationDisplayName apiOperationDisplayName) : base(apiOperationsDirectory.Path.Append(apiOperationDisplayName))
    {
        ApiOperationsDirectory = apiOperationsDirectory;
        ApiOperationDisplayName = apiOperationDisplayName;
    }

    public static ApiOperationDirectory From(ApiOperationsDirectory apiOperationsDirectory, ApiOperationDisplayName apiOperationDisplayName) => new(apiOperationsDirectory, apiOperationDisplayName);

    public static ApiOperationDirectory? TryFrom(ServiceDirectory serviceDirectory, DirectoryInfo? directory)
    {
        var parentDirectory = directory?.Parent;
        if (parentDirectory is not null)
        {
            var apiOperationsDirectory = ApiOperationsDirectory.TryFrom(serviceDirectory, parentDirectory);

            return apiOperationsDirectory is null ? null : From(apiOperationsDirectory, ApiOperationDisplayName.From(directory!.Name));
        }
        else
        {
            return null;
        }
    }
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