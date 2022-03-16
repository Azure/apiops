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

    public ServiceDirectory ServiceDirectory { get; }

    private GatewaysDirectory(ServiceDirectory serviceDirectory) : base(serviceDirectory.Path.Append(name))
    {
        ServiceDirectory = serviceDirectory;
    }

    public static GatewaysDirectory From(ServiceDirectory serviceDirectory) => new(serviceDirectory);

    public static GatewaysDirectory? TryFrom(ServiceDirectory serviceDirectory, DirectoryInfo? directory) =>
        name.Equals(directory?.Name) && serviceDirectory.PathEquals(directory.Parent)
        ? new(serviceDirectory)
        : null;
}

public sealed record GatewayDirectory : DirectoryRecord
{
    public GatewaysDirectory GatewaysDirectory { get; }
    public GatewayName GatewayName { get; }

    private GatewayDirectory(GatewaysDirectory gatewaysDirectory, GatewayName gatewayName) : base(gatewaysDirectory.Path.Append(gatewayName))
    {
        GatewaysDirectory = gatewaysDirectory;
        GatewayName = gatewayName;
    }

    public static GatewayDirectory From(GatewaysDirectory gatewaysDirectory, GatewayName gatewayName) => new(gatewaysDirectory, gatewayName);

    public static GatewayDirectory? TryFrom(ServiceDirectory serviceDirectory, DirectoryInfo? directory)
    {
        var parentDirectory = directory?.Parent;
        if (parentDirectory is not null)
        {
            var gatewaysDirectory = GatewaysDirectory.TryFrom(serviceDirectory, parentDirectory);

            return gatewaysDirectory is null ? null : From(gatewaysDirectory, GatewayName.From(directory!.Name));
        }
        else
        {
            return null;
        }
    }
}

public sealed record GatewayInformationFile : FileRecord
{
    private static readonly string name = "gatewayInformation.json";

    public GatewayDirectory GatewayDirectory { get; }

    private GatewayInformationFile(GatewayDirectory gatewayDirectory) : base(gatewayDirectory.Path.Append(name))
    {
        GatewayDirectory = gatewayDirectory;
    }

    public static GatewayInformationFile From(GatewayDirectory gatewayDirectory) => new(gatewayDirectory);

    public static GatewayInformationFile? TryFrom(ServiceDirectory serviceDirectory, FileInfo file)
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
