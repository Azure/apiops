using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace common.Models;

public sealed record Backend([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("properties")] Backend.BackendContractProperties Properties)
{
    public record BackendContractProperties
    {
        [JsonPropertyName("credentials")]
        public BackendCredentialsContract? Credentials { get; init; }

        [JsonPropertyName("description")]
        public string? Description { get; init; }

        [JsonPropertyName("properties")]
        public BackendProperties? Properties { get; init; }

        [JsonPropertyName("protocol")]
        public string? Protocol { get; init; }

        [JsonPropertyName("proxy")]
        public BackendProxyContract? Proxy { get; init; }

        [JsonPropertyName("resourceId")]
        public string? ResourceId { get; init; }

        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("tls")]
        public BackendTlsProperties? Tls { get; init; }

        [JsonPropertyName("url")]
        public string? Url { get; init; }
    }

    public record BackendCredentialsContract
    {
        [JsonPropertyName("authorization")]
        public BackendAuthorizationHeaderCredentials? Authorization { get; init; }

        [JsonPropertyName("certificate")]
        public string[]? Certificate { get; init; }

        [JsonPropertyName("certificateIds")]
        public string[]? CertificateIds { get; init; }

        [JsonPropertyName("header")]
        public JsonObject? Header { get; init; }

        [JsonPropertyName("query")]
        public JsonObject? Query { get; init; }
    }

    public record BackendAuthorizationHeaderCredentials
    {
        [JsonPropertyName("parameter")]
        public string? Parameter { get; init; }

        [JsonPropertyName("scheme")]
        public string? Scheme { get; init; }
    }

    public record BackendProperties
    {
        [JsonPropertyName("serviceFabricCluster")]
        public BackendServiceFabricClusterProperties? ServiceFabricCluster { get; init; }
    }

    public record BackendServiceFabricClusterProperties
    {
        [JsonPropertyName("clientCertificateId")]
        public string? ClientCertificateId { get; init; }

        [JsonPropertyName("clientCertificatethumbprint")]
        public string? ClientCertificatethumbprint { get; init; }

        [JsonPropertyName("managementEndpoints")]
        public string[]? ManagementEndpoints { get; init; }

        [JsonPropertyName("maxPartitionResolutionRetries")]
        public int? MaxPartitionResolutionRetries { get; init; }

        [JsonPropertyName("serverCertificateThumbprints")]
        public string[]? ServerCertificateThumbprints { get; init; }

        [JsonPropertyName("serverX509Names")]
        public X509CertificateName[]? ServerX509Names { get; init; }
    }

    public record X509CertificateName
    {
        [JsonPropertyName("issuerCertificateThumbprint")]
        public string? IssuerCertificateThumbprint { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }
    }

    public record BackendProxyContract
    {
        [JsonPropertyName("password")]
        public string? Password { get; init; }

        [JsonPropertyName("url")]
        public string? Url { get; init; }

        [JsonPropertyName("username")]
        public string? Username { get; init; }
    }

    public record BackendTlsProperties
    {
        [JsonPropertyName("validateCertificateChain")]
        public bool? ValidateCertificateChain { get; init; }

        [JsonPropertyName("validateCertificateName")]
        public bool? ValidateCertificateName { get; init; }
    }
}
