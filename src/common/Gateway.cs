using System;
using System.Text.Json.Serialization;

namespace common;

public sealed record GatewayResource : IResourceWithInformationFile
{
    private GatewayResource() { }

    public string FileName { get; } = "gatewayInformation.json";

    public string CollectionDirectoryName { get; } = "gateways";

    public string SingularName { get; } = "gateway";

    public string PluralName { get; } = "gateways";

    public string CollectionUriPath { get; } = "gateways";

    public Type DtoType { get; } = typeof(GatewayDto);

    public static GatewayResource Instance { get; } = new();
}

public sealed record GatewayDto
{
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public required GatewayContract Properties { get; init; }

    public sealed record GatewayContract
    {
        [JsonPropertyName("description")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Description { get; init; }

        [JsonPropertyName("locationData")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public ResourceLocationDataContract? LocationData { get; init; }
    }

    public sealed record ResourceLocationDataContract
    {
        [JsonPropertyName("city")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? City { get; init; }

        [JsonPropertyName("countryOrRegion")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? CountryOrRegion { get; init; }

        [JsonPropertyName("district")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? District { get; init; }

        [JsonPropertyName("name")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Name { get; init; }
    }
}
