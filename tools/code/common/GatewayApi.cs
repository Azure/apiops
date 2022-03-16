using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace common;

public sealed record GatewayApisFile : FileRecord
{
    private static readonly string name = "apis.json";

    public GatewayDirectory GatewayDirectory { get; }

    private GatewayApisFile(GatewayDirectory gatewayDirectory) : base(gatewayDirectory.Path.Append(name))
    {
        GatewayDirectory = gatewayDirectory;
    }

    public static GatewayApisFile From(GatewayDirectory gatewayDirectory) => new(gatewayDirectory);

    public static GatewayApisFile? TryFrom(ServiceDirectory serviceDirectory, FileInfo file)
    {
        if (name.Equals(file.Name))
        {
            var gatewayDirectory = GatewayDirectory.TryFrom(serviceDirectory, file.Directory);

            return gatewayDirectory is null ? null : new(gatewayDirectory);
        }
        else
        {
            return null;
        }
    }
}

public sealed record GatewayApiUri : UriRecord
{
    public GatewayApiUri(Uri value) : base(value)
    {
    }

    public static GatewayApiUri From(GatewayUri gatewayUri, ApiName apiName) =>
        new(UriExtensions.AppendPath(gatewayUri, "apis").AppendPath(apiName));
}

public sealed record GatewayApi([property: JsonPropertyName("name")] string Name)
{
    public JsonObject ToJsonObject() =>
        JsonSerializer.SerializeToNode(this)?.AsObject() ?? throw new InvalidOperationException("Could not serialize object.");

    public static GatewayApi FromJsonObject(JsonObject jsonObject) =>
        JsonSerializer.Deserialize<GatewayApi>(jsonObject) ?? throw new InvalidOperationException("Could not deserialize object.");

    public static Uri GetListByGatewayUri(GatewayUri gatewayUri) => UriExtensions.AppendPath(gatewayUri, "apis");
}