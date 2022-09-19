using System;
using System.Text.Json.Nodes;

namespace common;

public sealed record GatewaysUri : IArtifactUri
{
    public Uri Uri { get; }

    public GatewaysUri(ServiceUri serviceUri)
    {
        Uri = serviceUri.AppendPath("gateways");
    }
}

public sealed record GatewaysDirectory : IArtifactDirectory
{
    public static string Name { get; } = "gateways";

    public ArtifactPath Path { get; }

    public ServiceDirectory ServiceDirectory { get; }

    public GatewaysDirectory(ServiceDirectory serviceDirectory)
    {
        Path = serviceDirectory.Path.Append(Name);
        ServiceDirectory = serviceDirectory;
    }
}

public sealed record GatewayName
{
    private readonly string value;

    public GatewayName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Gateway name cannot be null or whitespace.", nameof(value));
        }

        this.value = value;
    }

    public override string ToString() => value;
}

public sealed record GatewayUri : IArtifactUri
{
    public Uri Uri { get; }

    public GatewayUri(GatewayName gatewayName, GatewaysUri gatewaysUri)
    {
        Uri = gatewaysUri.AppendPath(gatewayName.ToString());
    }
}

public sealed record GatewayDirectory : IArtifactDirectory
{
    public ArtifactPath Path { get; }

    public GatewaysDirectory GatewaysDirectory { get; }

    public GatewayDirectory(GatewayName gatewayName, GatewaysDirectory gatewaysDirectory)
    {
        Path = gatewaysDirectory.Path.Append(gatewayName.ToString());
        GatewaysDirectory = gatewaysDirectory;
    }
}

public sealed record GatewayInformationFile : IArtifactFile
{
    public static string Name { get; } = "gatewayInformation.json";

    public ArtifactPath Path { get; }

    public GatewayDirectory GatewayDirectory { get; }

    public GatewayInformationFile(GatewayDirectory gatewayDirectory)
    {
        Path = gatewayDirectory.Path.Append(Name);
        GatewayDirectory = gatewayDirectory;
    }
}

public sealed record GatewayModel
{
    public required string Name { get; init; }

    public required GatewayContractProperties Properties { get; init; }

    public sealed record GatewayContractProperties
    {
        public string? Description { get; init; }
        public ResourceLocationDataContract? LocationData { get; init; }

        public JsonObject Serialize() =>
            new JsonObject()
                .AddPropertyIfNotNull("description", Description)
                .AddPropertyIfNotNull("locationData", LocationData?.Serialize());

        public static GatewayContractProperties Deserialize(JsonObject jsonObject) =>
            new()
            {
                Description = jsonObject.TryGetStringProperty("description"),
                LocationData = jsonObject.TryGetJsonObjectProperty("locationData")
                                         .Map(ResourceLocationDataContract.Deserialize)
            };

        public sealed record ResourceLocationDataContract
        {
            public string? City { get; init; }
            public string? CountryOrRegion { get; init; }
            public string? District { get; init; }
            public string? Name { get; init; }

            public JsonObject Serialize() =>
                new JsonObject()
                    .AddPropertyIfNotNull("city", City)
                    .AddPropertyIfNotNull("countryOrRegion", CountryOrRegion)
                    .AddPropertyIfNotNull("district", District)
                    .AddPropertyIfNotNull("name", Name);

            public static ResourceLocationDataContract Deserialize(JsonObject jsonObject) =>
                new()
                {
                    City = jsonObject.TryGetStringProperty("city"),
                    CountryOrRegion = jsonObject.TryGetStringProperty("countryOrRegion"),
                    District = jsonObject.TryGetStringProperty("district"),
                    Name = jsonObject.TryGetStringProperty("name")
                };
        }
    }

    public JsonObject Serialize() =>
        new JsonObject()
            .AddProperty("properties", Properties.Serialize());

    public static GatewayModel Deserialize(GatewayName name, JsonObject jsonObject) =>
        new()
        {
            Name = jsonObject.TryGetStringProperty("name") ?? name.ToString(),
            Properties = jsonObject.GetJsonObjectProperty("properties")
                                   .Map(GatewayContractProperties.Deserialize)!
        };
}