using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace common.Models;

public sealed record Service([property: JsonPropertyName("name")] string Name,
                             [property: JsonPropertyName("location")] string Location,
                             [property: JsonPropertyName("sku")] Service.ApiManagementServiceSkuProperties Sku,
                             [property: JsonPropertyName("properties")] Service.ApiManagementServiceProperties Properties)
{
    [JsonPropertyName("tags")]
    public Dictionary<string, string>? Tags { get; set; }
    [JsonPropertyName("zones")]
    public string[]? Zones { get; set; }
    [JsonPropertyName("identity")]
    [JsonConverter(typeof(ApiManagementServiceIdentityConverter))]
    public ApiManagementServiceIdentity? Identity { get; set; }

    public sealed record ApiManagementServiceIdentity(string Type)
    {
        public string[]? UserAssignedIdentities { get; init; }
    }

    private class ApiManagementServiceIdentityConverter : JsonConverter<ApiManagementServiceIdentity>
    {
        public override ApiManagementServiceIdentity? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var node = JsonSerializer.Deserialize<JsonNode>(ref reader, options);

            if (node is null)
            {
                return null;
            }

            var jsonObject = node.AsObject();
            var type = jsonObject["type"]?.GetValue<string>() ?? throw new InvalidOperationException("Property 'type' is null.");
            var identities = jsonObject.TryGetPropertyValue("userAssignedIdentities", out var identitiesNode)
                ? identitiesNode?.AsObject().Select(kvp => kvp.Key).ToArray() ?? Array.Empty<string>()
                : null;

            return new(type) { UserAssignedIdentities = identities };
        }

        public override void Write(Utf8JsonWriter writer, ApiManagementServiceIdentity value, JsonSerializerOptions options)
        {
            var jsonObject = new JsonObject
            {
                { "type", value.Type }
            };

            if (value.UserAssignedIdentities is not null)
            {
                var identitiesObject = new JsonObject();
                foreach (var identity in value.UserAssignedIdentities)
                {
                    identitiesObject.Add(identity, new JsonObject());
                }

                jsonObject.Add("userAssignedIdentities", jsonObject);
            }

            JsonSerializer.Serialize(writer, jsonObject);
        }
    }

    public sealed record ApiManagementServiceProperties([property: JsonPropertyName("publisherEmail")] string PublisherEmail, [property: JsonPropertyName("publisherName")] string PublisherName)
    {
        [JsonPropertyName("additionalLocations")]
        public AdditionalLocation[]? AdditionalLocations { get; init; }
        [JsonPropertyName("apiVersionConstraint")]
        public ApiVersionConstraint? ApiVersionConstraint { get; init; }
        [JsonPropertyName("certificates")]
        public CertificateConfiguration[]? Certificates { get; init; }
        [JsonPropertyName("customProperties")]
        public Dictionary<string, string>? CustomProperties { get; init; } = new();
        [JsonPropertyName("disableGateway")]
        public bool? DisableGateway { get; init; }
        [JsonPropertyName("enableClientCertificate")]
        public bool? EnableClientCertificate { get; init; }
        [JsonPropertyName("hostnameConfigurations")]
        [JsonIgnore(Condition = JsonIgnoreCondition.Always)] // Ignore this property, we'll filter it in custom converter. APIM doesn't allow host name configurations ending with "azure-api.net"
        [JsonConverter(typeof(ApiManagementServicePropertiesConverter))]
        public HostnameConfiguration[]? HostnameConfigurations { get; init; }
        [JsonPropertyName("notificationSenderEmail")]
        public string? NotificationSenderEmail { get; init; }
        [JsonPropertyName("privateEndpointConnections")]
        public RemotePrivateEndpointConnectionWrapper[]? PrivateEndpointConnections { get; init; }
        [JsonPropertyName("publicIpAddressId")]
        public string? PublicIpAddressId { get; init; }
        [JsonPropertyName("publicNetworkAccess")]
        public string? PublicNetworkAccess { get; init; }
        [JsonPropertyName("restore")]
        public bool? Restore { get; init; }
        [JsonPropertyName("virtualNetworkConfiguration")]
        public VirtualNetworkConfiguration? VirtualNetworkConfiguration { get; init; }
        [JsonPropertyName("virtualNetworkType")]
        public string? VirtualNetworkType { get; init; }
    }

    private class ApiManagementServicePropertiesConverter : JsonConverter<ApiManagementServiceProperties>
    {
        public override ApiManagementServiceProperties? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var properties = document.Deserialize<ApiManagementServiceProperties>(options);

            var jsonObject = JsonObject.Create(document.RootElement);
            var hostNameConfigurations = jsonObject?.TryGetPropertyValue("hostnameConfigurations", out var node) ?? throw new InvalidOperationException("Could not deserialize properties.")
                ? node?.AsArray()
                       .Select(hostNameNode => hostNameNode.Deserialize<HostnameConfiguration>(options))
                       .Where(hostNameConfiguration => hostNameConfiguration is not null)
                       .Select(hostNameConfiguration => hostNameConfiguration!)
                       .ToArray()
                : null;

            return properties is null ? properties : properties with { HostnameConfigurations = hostNameConfigurations };
        }

        public override void Write(Utf8JsonWriter writer, ApiManagementServiceProperties value, JsonSerializerOptions options)
        {
            // API doesn't allow host name configurations ending with "azure-api.net"
            var filteredValue =
                value with
                {
                    HostnameConfigurations =
                        value.HostnameConfigurations?.All(configuration => configuration.HostName.EndsWith("azure-api.net")) ?? false
                        ? null
                        : value.HostnameConfigurations?.Where(configuration => configuration.HostName.EndsWith("azure-api.net") is false)?.ToArray()
                };

            var jsonObject = JsonSerializer.SerializeToNode(value, options)?.AsObject() ?? throw new InvalidOperationException("Could not serialize properties.");
            if (filteredValue.HostnameConfigurations is not null)
            {
                jsonObject.Add("hostnameConfigurations", JsonSerializer.SerializeToNode(filteredValue.HostnameConfigurations, options));
            }

            JsonSerializer.Serialize(writer, jsonObject);
        }
    }

    public sealed record AdditionalLocation([property: JsonPropertyName("location")] string Location, [property: JsonPropertyName("sku")] ApiManagementServiceSkuProperties Sku)
    {
        [JsonPropertyName("disableGateway")]
        public bool? DisableGateway { get; init; }
        [JsonPropertyName("publicIpAddressId")]
        public string? PublicIpAddressId { get; init; }
        [JsonPropertyName("virtualNetworkConfiguration")]
        public VirtualNetworkConfiguration? VirtualNetworkConfiguration { get; init; }
        [JsonPropertyName("zones")]
        public string[]? Zones { get; init; }
    }

    public sealed record ApiManagementServiceSkuProperties([property: JsonPropertyName("capacity")] int Capacity)
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }
    }

    public sealed record VirtualNetworkConfiguration
    {
        [JsonPropertyName("subnetResourceId")]
        public string? SubnetResourceId { get; init; }
    }

    public sealed record ApiVersionConstraint
    {
        [JsonPropertyName("minApiVersion")]
        public string? MinApiVersion { get; init; }
    }

    public sealed record CertificateConfiguration()
    {
        [JsonPropertyName("certificate")]
        public CertificateInformation? Certificate { get; init; }
        [JsonPropertyName("certificatePassword")]
        public string? CertificatePassword { get; init; }
        [JsonPropertyName("encodedCertificate")]
        public string? EncodedCertificate { get; init; }
        [JsonPropertyName("storeName")]
        public string? StoreName { get; init; }
    }

    public sealed record HostnameConfiguration([property: JsonPropertyName("hostName")] string HostName)
    {
        [JsonPropertyName("certificate")]
        public CertificateInformation? Certificate { get; init; }
        [JsonPropertyName("certificatePassword")]
        public string? CertificatePassword { get; init; }
        [JsonPropertyName("certificateSource")]
        public string? CertificateSource { get; init; }
        [JsonPropertyName("certificateStatus")]
        public string? CertificateStatus { get; init; }
        [JsonPropertyName("defaultSslBinding")]
        public bool? DefaultSslBinding { get; init; }
        [JsonPropertyName("encodedCertificate")]
        public string? EncodedCertificate { get; init; }
        [JsonPropertyName("identityClientId")]
        public string? IdentityClientId { get; init; }
        [JsonPropertyName("keyVaultId")]
        public string? KeyVaultId { get; init; }
        [JsonPropertyName("negotiateClientCertificate")]
        public bool? NegotiateClientCertificate { get; init; }
        [JsonPropertyName("type")]
        public string? Type { get; init; }
    }

    public sealed record CertificateInformation([property: JsonPropertyName("expiry")] string Expiry,
                                                [property: JsonPropertyName("subject")] string Subject,
                                                [property: JsonPropertyName("thumbprint")] string Thumbprint);

    public sealed record RemotePrivateEndpointConnectionWrapper
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }
        [JsonPropertyName("name")]
        public string? Name { get; init; }
        [JsonPropertyName("properties")]
        public PrivateEndpointConnectionWrapperProperties? Properties { get; init; }
        [JsonPropertyName("type")]
        public string? Type { get; init; }
    }

    public sealed record PrivateEndpointConnectionWrapperProperties([property: JsonPropertyName("privateLinkServiceConnectionState")] PrivateLinkServiceConnectionState PrivateLinkServiceConnectionState);

    public sealed record PrivateLinkServiceConnectionState
    {
        [JsonPropertyName("actionsRequired")]
        public string? ActionsRequired { get; init; }
        [JsonPropertyName("description")]
        public string? Description { get; init; }
        [JsonPropertyName("status")]
        public string? Status { get; init; }
    }
}
