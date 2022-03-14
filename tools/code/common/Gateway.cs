using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace common;

public sealed record GatewayName : NonEmptyString
{
    private GatewayName(string value) : base(value)
    {
    }

    public static GatewayName From(string value) => new(value);

    public static GatewayName From(GatewayInformationFile file)
    {
        var jsonObject = file.ReadAsJsonObject();
        var gateway = Gateway.FromJsonObject(jsonObject);

        return new GatewayName(gateway.Name);
    }
}

public sealed record GatewayUri : UriRecord
{
    public GatewayUri(Uri value) : base(value)
    {
    }

    public static GatewayUri From(ServiceUri serviceUri, GatewayName gatewayName) =>
        new(UriExtensions.AppendPath(serviceUri, "gateways").AppendPath(gatewayName));
}

public sealed record GatewaysDirectory : DirectoryRecord
{
    private static readonly string name = "gateways";

    private GatewaysDirectory(RecordPath path) : base(path)
    {
    }

    public static GatewaysDirectory From(ServiceDirectory serviceDirectory) =>
        new(serviceDirectory.Path.Append(name));

    public static GatewaysDirectory? TryFrom(ServiceDirectory serviceDirectory, DirectoryInfo? directory) =>
        name.Equals(directory?.Name) && serviceDirectory.Path.PathEquals(directory.Parent?.FullName)
        ? new(RecordPath.From(directory.FullName))
        : null;
}

public sealed record GatewayInformationFile : FileRecord
{
    private static readonly string name = "gatewayInformation.json";

    public GatewayInformationFile(RecordPath path) : base(path)
    {
    }

    public static GatewayInformationFile From(GatewaysDirectory gatewaysDirectory, GatewayName gatewayName)
        => new(gatewaysDirectory.Path.Append(gatewayName)
                                     .Append(name));

    public static GatewayInformationFile? TryFrom(ServiceDirectory serviceDirectory, FileInfo file) =>
        name.Equals(file.Name) && GatewaysDirectory.TryFrom(serviceDirectory, file.Directory?.Parent) is not null
        ? new(RecordPath.From(file.FullName))
        : null;
}

public sealed record Gateway([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("properties")] Gateway.GatewayContractProperties Properties)
{
    public record GatewayContractProperties
    {
        [JsonPropertyName("description")]
        public string? Description { get; init; }

        [JsonPropertyName("locationData")]
        public ResourceLocationDataContract? LocationData { get; init; }
    }

    public record ResourceLocationDataContract([property: JsonPropertyName("name")] string Name)
    {
        [JsonPropertyName("city")]
        public string? City { get; init; }

        [JsonPropertyName("countryOrRegion")]
        public string? CountryOrRegion { get; init; }
        [JsonPropertyName("district")]
        public string? District { get; init; }
    }

    public JsonObject ToJsonObject() =>
        JsonSerializer.SerializeToNode(this)?.AsObject() ?? throw new InvalidOperationException("Could not serialize object.");

    public static Gateway FromJsonObject(JsonObject jsonObject) =>
        JsonSerializer.Deserialize<Gateway>(jsonObject) ?? throw new InvalidOperationException("Could not deserialize object.");

    public static Uri GetListByServiceUri(ServiceUri serviceUri) => UriExtensions.AppendPath(serviceUri, "gateways");
}
