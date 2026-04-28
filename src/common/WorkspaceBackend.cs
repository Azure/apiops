using System;
using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace common;

public sealed record WorkspaceBackendResource : IResourceWithInformationFile, IChildResource
{
    private WorkspaceBackendResource() { }

    public string FileName { get; } = "backendInformation.json";

    public string CollectionDirectoryName { get; } = "backends";

    public string SingularName { get; } = "backend";

    public string PluralName { get; } = "backends";

    public string CollectionUriPath { get; } = "backends";

    public Type DtoType { get; } = typeof(WorkspaceBackendDto);

    public IResource Parent { get; } = WorkspaceResource.Instance;

    public static WorkspaceBackendResource Instance { get; } = new();
}

public sealed record WorkspaceBackendDto
{
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public required BackendContract Properties { get; init; }

    public sealed record BackendContract
    {
        [JsonPropertyName("circuitBreaker")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public BackendCircuitBreaker? CircuitBreaker { get; init; }

        [JsonPropertyName("credentials")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public BackendCredentialsContract? Credentials { get; init; }

        [JsonPropertyName("description")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Description { get; init; }

        [JsonPropertyName("pool")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public Pool? Pool { get; init; }

        [JsonPropertyName("properties")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public BackendProperties? Properties { get; init; }

        [JsonPropertyName("protocol")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Protocol { get; init; }

        [JsonPropertyName("proxy")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public BackendProxyContract? Proxy { get; init; }

        [JsonPropertyName("resourceId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? ResourceId { get; init; }

        [JsonPropertyName("title")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Title { get; init; }

        [JsonPropertyName("tls")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public BackendTlsProperties? Tls { get; init; }

        [JsonPropertyName("type")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Type { get; init; }

        [JsonPropertyName("url")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
#pragma warning disable CA1056 // URI-like properties should not be strings
        public string? Url { get; init; }
#pragma warning restore CA1056 // URI-like properties should not be strings
    }

    public sealed record BackendCircuitBreaker
    {
        [JsonPropertyName("rules")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public ImmutableArray<CircuitBreakerRule>? Rules { get; init; }
    }

    public sealed record CircuitBreakerRule
    {
        [JsonPropertyName("acceptRetryAfter")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool? AcceptRetryAfter { get; init; }

        [JsonPropertyName("failureCondition")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public CircuitBreakerFailureCondition? FailureCondition { get; init; }

        [JsonPropertyName("name")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Name { get; init; }

        [JsonPropertyName("tripDuration")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? TripDuration { get; init; }
    }

    public sealed record CircuitBreakerFailureCondition
    {
        [JsonPropertyName("count")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public long? Count { get; init; }

        [JsonPropertyName("errorReasons")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public ImmutableArray<string>? ErrorReasons { get; init; }

        [JsonPropertyName("interval")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Interval { get; init; }

        [JsonPropertyName("percentage")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public long? Percentage { get; init; }

        [JsonPropertyName("statusCodeRanges")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public ImmutableArray<FailureStatusCodeRange>? StatusCodeRanges { get; init; }
    }

    public sealed record FailureStatusCodeRange
    {
        [JsonPropertyName("max")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int? Max { get; init; }

        [JsonPropertyName("min")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int? Min { get; init; }
    }

    public sealed record BackendCredentialsContract
    {
        [JsonPropertyName("authorization")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public BackendAuthorizationHeaderCredentials? Authorization { get; init; }

        [JsonPropertyName("certificate")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public ImmutableArray<string>? Certificate { get; init; }

        [JsonPropertyName("certificateIds")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public ImmutableArray<string>? CertificateIds { get; init; }

        [JsonPropertyName("header")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public ImmutableDictionary<string, ImmutableArray<string>>? Header { get; init; }

        [JsonPropertyName("query")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public ImmutableDictionary<string, ImmutableArray<string>>? Query { get; init; }
    }

    public sealed record BackendAuthorizationHeaderCredentials
    {
        [JsonPropertyName("parameter")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Parameter { get; init; }

        [JsonPropertyName("scheme")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Scheme { get; init; }
    }

    public sealed record Pool
    {
        [JsonPropertyName("services")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public ImmutableArray<BackendPoolItem>? Services { get; init; }
    }

    public sealed record BackendPoolItem
    {
        [JsonPropertyName("id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Id { get; init; }

        [JsonPropertyName("priority")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int? Priority { get; init; }

        [JsonPropertyName("weight")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int? Weight { get; init; }
    }

    public sealed record BackendProperties
    {
        [JsonPropertyName("serviceFabricCluster")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public BackendServiceFabricClusterProperties? ServiceFabricCluster { get; init; }
    }

    public sealed record BackendServiceFabricClusterProperties
    {
        [JsonPropertyName("clientCertificateId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? ClientCertificateId { get; init; }

        [JsonPropertyName("clientCertificatethumbprint")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? ClientCertificateThumbprint { get; init; }

        [JsonPropertyName("managementEndpoints")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public ImmutableArray<string>? ManagementEndpoints { get; init; }

        [JsonPropertyName("maxPartitionResolutionRetries")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int? MaxPartitionResolutionRetries { get; init; }

        [JsonPropertyName("serverCertificateThumbprints")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public ImmutableArray<string>? ServerCertificateThumbprints { get; init; }

        [JsonPropertyName("serverX509Names")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public ImmutableArray<X509CertificateName>? ServerX509Names { get; init; }
    }

    public sealed record X509CertificateName
    {
        [JsonPropertyName("issuerCertificateThumbprint")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? IssuerCertificateThumbprint { get; init; }

        [JsonPropertyName("name")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Name { get; init; }
    }

    public sealed record BackendProxyContract
    {
        [JsonPropertyName("password")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Password { get; init; }

        [JsonPropertyName("url")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
#pragma warning disable CA1056 // URI-like properties should not be strings
        public string? Url { get; init; }
#pragma warning restore CA1056 // URI-like properties should not be strings

        [JsonPropertyName("username")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Username { get; init; }
    }

    public sealed record BackendTlsProperties
    {
        [JsonPropertyName("validateCertificateChain")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool? ValidateCertificateChain { get; init; }

        [JsonPropertyName("validateCertificateName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool? ValidateCertificateName { get; init; }
    }
}
