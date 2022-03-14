using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace common;

public sealed record GatewayApisFile : FileRecord
{
    private static readonly string name = "apis.json";
    private readonly GatewaysDirectory gatewaysDirectory;
    private readonly GatewayName gatewayName;

    private GatewayApisFile(GatewaysDirectory gatewaysDirectory, GatewayName gatewayName)
        : base(gatewaysDirectory.Path.Append(gatewayName).Append(name))
    {
        this.gatewaysDirectory = gatewaysDirectory;
        this.gatewayName = gatewayName;
    }

    public GatewayInformationFile GetGatewayInformationFile() => GatewayInformationFile.From(gatewaysDirectory, gatewayName);

    public static GatewayApisFile From(GatewaysDirectory gatewaysDirectory, GatewayName displayName)
        => new(gatewaysDirectory, displayName);

    public static GatewayApisFile? TryFrom(ServiceDirectory serviceDirectory, FileInfo file)
    {
        if (name.Equals(file.Name) is false)
        {
            return null;
        }

        var directory = file.Directory;
        if (directory is null)
        {
            return null;
        }

        var gatewaysDirectory = GatewaysDirectory.TryFrom(serviceDirectory, directory.Parent);
        return gatewaysDirectory is null
            ? null
            : new(gatewaysDirectory, GatewayName.From(directory.Name));
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