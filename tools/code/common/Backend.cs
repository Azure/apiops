using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace common;

public sealed record BackendsUri : IArtifactUri
{
    public Uri Uri { get; }

    public BackendsUri(ServiceUri serviceUri)
    {
        Uri = serviceUri.AppendPath("backends");
    }
}

public sealed record BackendsDirectory : IArtifactDirectory
{
    public static string Name { get; } = "backends";

    public ArtifactPath Path { get; }

    public ServiceDirectory ServiceDirectory { get; }

    public BackendsDirectory(ServiceDirectory serviceDirectory)
    {
        Path = serviceDirectory.Path.Append(Name);
        ServiceDirectory = serviceDirectory;
    }
}

public sealed record BackendName
{
    private readonly string value;

    public BackendName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Backend name cannot be null or whitespace.", nameof(value));
        }

        this.value = value;
    }

    public override string ToString() => value;
}

public sealed record BackendUri : IArtifactUri
{
    public Uri Uri { get; }

    public BackendUri(BackendName backendName, BackendsUri backendsUri)
    {
        Uri = backendsUri.AppendPath(backendName.ToString());
    }
}

public sealed record BackendDirectory : IArtifactDirectory
{
    public ArtifactPath Path { get; }

    public BackendsDirectory BackendsDirectory { get; }

    public BackendDirectory(BackendName backendName, BackendsDirectory backendsDirectory)
    {
        Path = backendsDirectory.Path.Append(backendName.ToString());
        BackendsDirectory = backendsDirectory;
    }
}

public sealed record BackendInformationFile : IArtifactFile
{
    public static string Name { get; } = "backendInformation.json";

    public ArtifactPath Path { get; }

    public BackendDirectory BackendDirectory { get; }

    public BackendInformationFile(BackendDirectory backendDirectory)
    {
        Path = backendDirectory.Path.Append(Name);
        BackendDirectory = backendDirectory;
    }
}

public sealed record BackendModel
{
    public required string Name { get; init; }

    public required BackendContractProperties Properties { get; init; }

    public sealed record BackendContractProperties
    {
        public BackendCredentialsContract? Credentials { get; init; }
        public string? Description { get; init; }
        public BackendProperties? Properties { get; init; }
        public BackendProtocolOption? Protocol { get; init; }
        public BackendProxyContract? Proxy { get; init; }
        public string? ResourceId { get; init; }
        public string? Title { get; init; }
        public BackendTlsProperties? Tls { get; init; }
        public string? Url { get; init; }

        public JsonObject Serialize() =>
            new JsonObject()
                .AddPropertyIfNotNull("credentials", Credentials?.Serialize())
                .AddPropertyIfNotNull("description", Description)
                .AddPropertyIfNotNull("properties", Properties?.Serialize())
                .AddPropertyIfNotNull("protocol", Protocol?.Serialize())
                .AddPropertyIfNotNull("proxy", Proxy?.Serialize())
                .AddPropertyIfNotNull("resourceId", ResourceId)
                .AddPropertyIfNotNull("title", Title)
                .AddPropertyIfNotNull("tls", Tls?.Serialize())
                .AddPropertyIfNotNull("url", Url);

        public static BackendContractProperties Deserialize(JsonObject jsonObject) =>
            new()
            {
                Credentials = jsonObject.TryGetJsonObjectProperty("credentials")
                                          .Map(BackendCredentialsContract.Deserialize),
                Description = jsonObject.TryGetStringProperty("description"),
                Properties = jsonObject.TryGetJsonObjectProperty("properties")
                                    .Map(BackendProperties.Deserialize),
                Protocol = jsonObject.TryGetProperty("protocol")
                                     .Map(BackendProtocolOption.Deserialize),
                Proxy = jsonObject.TryGetJsonObjectProperty("proxy")
                                  .Map(BackendProxyContract.Deserialize),
                ResourceId = jsonObject.TryGetStringProperty("resourceId"),
                Title = jsonObject.TryGetStringProperty("title"),
                Tls = jsonObject.TryGetJsonObjectProperty("tls")
                                .Map(BackendTlsProperties.Deserialize),
                Url = jsonObject.TryGetStringProperty("url")
            };

        public sealed record BackendCredentialsContract
        {
            public BackendAuthorizationHeaderCredentials? Authorization { get; init; }
            public string[]? Certificate { get; init; }
            public string[]? CertificateIds { get; init; }
            public Dictionary<string, string[]>? Header { get; init; }
            public Dictionary<string, string[]>? Query { get; init; }

            public JsonObject Serialize() =>
                new JsonObject()
                    .AddPropertyIfNotNull("authorization", Authorization?.Serialize())
                    .AddPropertyIfNotNull("certificate", Certificate?.Choose(x => (JsonNode?)x)
                                                                    ?.ToJsonArray())
                    .AddPropertyIfNotNull("certificateIds", CertificateIds?.Choose(x => (JsonNode?)x)
                                                                       ?.ToJsonArray())
                    .AddPropertyIfNotNull("header", Header?.ToJsonObject())
                    .AddPropertyIfNotNull("query", Query?.ToJsonObject());

            public static BackendCredentialsContract Deserialize(JsonObject jsonObject) =>
                new()
                {
                    Authorization = jsonObject.TryGetJsonObjectProperty("authorization")
                                              .Map(BackendAuthorizationHeaderCredentials.Deserialize),
                    Certificate = jsonObject.TryGetJsonArrayProperty("certificate")
                                           ?.Choose(node => node?.GetValue<string>())
                                           ?.ToArray(),
                    CertificateIds = jsonObject.TryGetJsonArrayProperty("certificateIds")
                                              ?.Choose(node => node?.GetValue<string>())
                                              ?.ToArray(),
                    Header = jsonObject.TryGetJsonObjectProperty("header")
                                      ?.Where(kvp => kvp.Value is not null)
                                      ?.ToDictionary(kvp => kvp.Key,
                                                     kvp => kvp.Value!.AsArray()
                                                                      .Choose(node => node?.GetValue<string>())
                                                                      .ToArray()),
                    Query = jsonObject.TryGetJsonObjectProperty("query")
                                     ?.Where(kvp => kvp.Value is not null)
                                     ?.ToDictionary(kvp => kvp.Key,
                                                    kvp => kvp.Value!.AsArray()
                                                                     .Choose(node => node?.GetValue<string>())
                                                                     .ToArray())
                };

            public sealed record BackendAuthorizationHeaderCredentials
            {
                public string? Parameter { get; init; }
                public string? Scheme { get; init; }

                public JsonObject Serialize() =>
                    new JsonObject()
                        .AddPropertyIfNotNull("parameter", Parameter)
                        .AddPropertyIfNotNull("scheme", Scheme);

                public static BackendAuthorizationHeaderCredentials Deserialize(JsonObject jsonObject) =>
                    new()
                    {
                        Parameter = jsonObject.TryGetStringProperty("parameter"),
                        Scheme = jsonObject.TryGetStringProperty("scheme")
                    };
            }
        }

        public sealed record BackendProperties
        {
            public BackendServiceFabricClusterProperties? ServiceFabricCluster { get; init; }

            public JsonObject Serialize() =>
                new JsonObject()
                    .AddPropertyIfNotNull("serviceFabricCluster", ServiceFabricCluster?.Serialize());

            public static BackendProperties Deserialize(JsonObject jsonObject) =>
                new()
                {
                    ServiceFabricCluster = jsonObject.TryGetJsonObjectProperty("serviceFabricCluster")
                                                     .Map(BackendServiceFabricClusterProperties.Deserialize)
                };

            public sealed record BackendServiceFabricClusterProperties
            {
                public string? ClientCertificateId { get; init; }
                public string? ClientCertificateThumbprint { get; init; }
                public string[]? ManagementEndpoints { get; init; }
                public int? MaxPartitionResolutionRetries { get; init; }
                public string[]? ServerCertificateThumbprints { get; init; }
                public X509CertificateName[]? ServerX509Names { get; init; }

                public JsonObject Serialize() =>
                    new JsonObject()
                        .AddPropertyIfNotNull("clientCertificateId", ClientCertificateId)
                        .AddPropertyIfNotNull("clientCertificatethumbprint", ClientCertificateThumbprint)
                        .AddPropertyIfNotNull("managementEndpoints", ManagementEndpoints?.Select(JsonNodeExtensions.FromString)
                                                                                        ?.ToJsonArray())
                        .AddPropertyIfNotNull("maxPartitionResolutionRetries", MaxPartitionResolutionRetries)
                        .AddPropertyIfNotNull("serverCertificateThumbprints", ServerCertificateThumbprints?.Select(JsonNodeExtensions.FromString)
                                                                                                          ?.ToJsonArray())
                        .AddPropertyIfNotNull("serverX509Names", ServerX509Names?.Select(x => x.Serialize())
                                                                                ?.ToJsonArray());

                public static BackendServiceFabricClusterProperties Deserialize(JsonObject jsonObject) =>
                    new()
                    {
                        ClientCertificateId = jsonObject.TryGetStringProperty("clientCertificateId"),
                        ClientCertificateThumbprint = jsonObject.TryGetStringProperty("clientCertificatethumbprint"),
                        ManagementEndpoints = jsonObject.TryGetJsonArrayProperty("managementEndpoints")
                                                       ?.Choose(node => node?.GetValue<string>())
                                                       ?.ToArray(),
                        MaxPartitionResolutionRetries = jsonObject.TryGetIntProperty("maxPartitionResolutionRetries"),
                        ServerCertificateThumbprints = jsonObject.TryGetJsonArrayProperty("serverCertificateThumbprints")
                                                       ?.Choose(node => node?.GetValue<string>())
                                                       ?.ToArray(),
                        ServerX509Names = jsonObject.TryGetJsonArrayProperty("serverX509Names")
                                                   ?.Choose(node => node?.AsObject())
                                                   ?.Select(X509CertificateName.Deserialize)
                                                   ?.ToArray()
                    };

                public sealed record X509CertificateName
                {
                    public string? IssuerCertificateThumbprint { get; init; }
                    public string? Name { get; init; }

                    public JsonObject Serialize() =>
                        new JsonObject()
                            .AddPropertyIfNotNull("issuerCertificateThumbprint", IssuerCertificateThumbprint)
                            .AddPropertyIfNotNull("name", Name);

                    public static X509CertificateName Deserialize(JsonObject jsonObject) =>
                        new()
                        {
                            IssuerCertificateThumbprint = jsonObject.TryGetStringProperty("issuerCertificateThumbprint"),
                            Name = jsonObject.TryGetStringProperty("name")
                        };
                }
            }
        }

        public sealed record BackendProtocolOption
        {
            private readonly string value;

            private BackendProtocolOption(string value)
            {
                this.value = value;
            }

            public static BackendProtocolOption Http => new("http");
            public static BackendProtocolOption Soap => new("soap");

            public override string ToString() => value;

            public JsonNode Serialize() => JsonValue.Create(ToString()) ?? throw new JsonException("Value cannot be null.");

            public static BackendProtocolOption Deserialize(JsonNode node) =>
                node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var value)
                    ? value switch
                    {
                        _ when nameof(Http).Equals(value, StringComparison.OrdinalIgnoreCase) => Http,
                        _ when nameof(Soap).Equals(value, StringComparison.OrdinalIgnoreCase) => Soap,
                        _ => throw new JsonException($"'{value}' is not a valid {nameof(BackendProtocolOption)}.")
                    }
                        : throw new JsonException("Node must be a string JSON value.");
        }

        public sealed record BackendProxyContract
        {
            public string? Password { get; init; }
            public string? Url { get; init; }
            public string? Username { get; init; }

            public JsonObject Serialize() =>
                new JsonObject()
                    .AddPropertyIfNotNull("password", Password)
                    .AddPropertyIfNotNull("url", Url)
                    .AddPropertyIfNotNull("username", Username);

            public static BackendProxyContract Deserialize(JsonObject jsonObject) =>
                new()
                {
                    Password = jsonObject.TryGetStringProperty("password"),
                    Url = jsonObject.TryGetStringProperty("url"),
                    Username = jsonObject.TryGetStringProperty("username")
                };
        }

        public sealed record BackendTlsProperties
        {
            public bool? ValidateCertificateChain { get; init; }
            public bool? ValidateCertificateName { get; init; }

            public JsonObject Serialize() =>
                new JsonObject()
                    .AddPropertyIfNotNull("validateCertificateChain", ValidateCertificateChain)
                    .AddPropertyIfNotNull("validateCertificateName", ValidateCertificateName);

            public static BackendTlsProperties Deserialize(JsonObject jsonObject) =>
                new()
                {
                    ValidateCertificateChain = jsonObject.TryGetBoolProperty("validateCertificateChain"),
                    ValidateCertificateName = jsonObject.TryGetBoolProperty("validateCertificateName")
                };
        }
    }

    public JsonObject Serialize() =>
        new JsonObject()
            .AddProperty("properties", Properties.Serialize());

    public static BackendModel Deserialize(BackendName name, JsonObject jsonObject) =>
        new()
        {
            Name = jsonObject.TryGetStringProperty("name") ?? name.ToString(),
            Properties = jsonObject.GetJsonObjectProperty("properties")
                                   .Map(BackendContractProperties.Deserialize)!
        };
}