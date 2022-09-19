using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace common;

public sealed record ApisUri : IArtifactUri
{
    public Uri Uri { get; }

    public ApisUri(ServiceUri serviceUri)
    {
        Uri = serviceUri.AppendPath("apis");
    }
}

public sealed record ApisDirectory : IArtifactDirectory
{
    public static string Name { get; } = "apis";

    public ArtifactPath Path { get; }

    public ServiceDirectory ServiceDirectory { get; }

    public ApisDirectory(ServiceDirectory serviceDirectory)
    {
        Path = serviceDirectory.Path.Append(Name);
        ServiceDirectory = serviceDirectory;
    }
}

public sealed record ApiName
{
    private readonly string value;

    public ApiName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"API name cannot be null or whitespace.", nameof(value));
        }

        this.value = value;
    }

    public override string ToString() => value;
}

public sealed record ApiUri : IArtifactUri
{
    public Uri Uri { get; }

    public ApiUri(ApiName apiName, ApisUri apisUri)
    {
        Uri = apisUri.AppendPath(apiName.ToString());
    }
}

public sealed record ApiDirectory : IArtifactDirectory
{
    public ArtifactPath Path { get; }

    public ApisDirectory ApisDirectory { get; }

    public ApiDirectory(ApiName apiName, ApisDirectory apisDirectory)
    {
        Path = apisDirectory.Path.Append(apiName.ToString());
        ApisDirectory = apisDirectory;
    }
}

public sealed record ApiInformationFile : IArtifactFile
{
    public static string Name { get; } = "apiInformation.json";

    public ArtifactPath Path { get; }

    public ApiDirectory ApiDirectory { get; }

    public string ApiName => ApiDirectory.GetName();

    public ApiInformationFile(ApiDirectory apiDirectory)
    {
        Path = apiDirectory.Path.Append(Name);
        ApiDirectory = apiDirectory;
    }
}

public sealed record ApiModel
{
    public required string Name { get; init; }

    public required ApiCreateOrUpdateProperties Properties { get; init; }

    public sealed record ApiCreateOrUpdateProperties
    {
        public string? ApiRevision { get; init; }
        public string? ApiRevisionDescription { get; init; }
        public ApiTypeOption? ApiType { get; init; }
        public string? ApiVersion { get; init; }
        public string? ApiVersionDescription { get; init; }
        public ApiVersionSetContractDetails? ApiVersionSet { get; init; }
        public string? ApiVersionSetId { get; init; }
        public AuthenticationSettingsContract? AuthenticationSettings { get; init; }
        public ApiContactInformation? Contact { get; init; }
        public string? Description { get; init; }
        public string? DisplayName { get; init; }
        public ApiFormatOption? Format { get; init; }
        public bool? IsCurrent { get; init; }
        public ApiLicenseInformation? License { get; init; }
        public string? Path { get; init; }
        public ProtocolOption[]? Protocols { get; init; }
        public string? ServiceUrl { get; init; }
        public string? SourceApiId { get; init; }
        public SubscriptionKeyParameterNamesContract? SubscriptionKeyParameterNames { get; init; }
        public bool? SubscriptionRequired { get; init; }
        public string? TermsOfServiceUrl { get; init; }
        public ApiTypeOption? Type { get; init; }
        public string? Value { get; init; }
        public ApiCreateOrUpdatePropertiesWsdlSelector? WsdlSelector { get; init; }

        public JsonObject Serialize() =>
            new JsonObject()
                .AddPropertyIfNotNull("apiRevision", ApiRevision)
                .AddPropertyIfNotNull("apiRevisionDescription", ApiRevisionDescription)
                .AddPropertyIfNotNull("apiType", ApiType?.Serialize())
                .AddPropertyIfNotNull("apiVersion", ApiVersion)
                .AddPropertyIfNotNull("apiVersionDescription", ApiVersionDescription)
                .AddPropertyIfNotNull("apiVersionSet", ApiVersionSet?.Serialize())
                .AddPropertyIfNotNull("apiVersionSetId", ApiVersionSetId)
                .AddPropertyIfNotNull("authenticationSettings", AuthenticationSettings?.Serialize())
                .AddPropertyIfNotNull("contact", Contact?.Serialize())
                .AddPropertyIfNotNull("description", Description)
                .AddPropertyIfNotNull("displayName", DisplayName)
                .AddPropertyIfNotNull("format", Format?.Serialize())
                .AddPropertyIfNotNull("isCurrent", IsCurrent)
                .AddPropertyIfNotNull("license", License?.Serialize())
                .AddPropertyIfNotNull("path", Path)
                .AddPropertyIfNotNull("protocols", Protocols?.Select(protocol => protocol.Serialize())
                                                            ?.ToJsonArray())
                .AddPropertyIfNotNull("serviceUrl", ServiceUrl)
                .AddPropertyIfNotNull("sourceApiId", SourceApiId)
                .AddPropertyIfNotNull("subscriptionKeyParameterNames", SubscriptionKeyParameterNames?.Serialize())
                .AddPropertyIfNotNull("subscriptionRequired", SubscriptionRequired)
                .AddPropertyIfNotNull("termsOfServiceUrl", TermsOfServiceUrl)
                .AddPropertyIfNotNull("type", Type?.Serialize())
                .AddPropertyIfNotNull("value", Value)
                .AddPropertyIfNotNull("wsdlSelector", WsdlSelector?.Serialize());

        public static ApiCreateOrUpdateProperties Deserialize(JsonObject jsonObject) =>
            new()
            {
                ApiRevision = jsonObject.TryGetStringProperty("apiRevision"),
                ApiRevisionDescription = jsonObject.TryGetStringProperty("apiRevisionDescription"),
                ApiType = jsonObject.TryGetProperty("apiType")
                                    .Map(ApiTypeOption.Deserialize),
                ApiVersion = jsonObject.TryGetStringProperty("apiVersion"),
                ApiVersionDescription = jsonObject.TryGetStringProperty("apiVersionDescription"),
                ApiVersionSet = jsonObject.TryGetJsonObjectProperty("apiVersionSet")
                                          .Map(ApiVersionSetContractDetails.Deserialize),
                ApiVersionSetId = jsonObject.TryGetStringProperty("apiVersionSetId"),
                AuthenticationSettings = jsonObject.TryGetJsonObjectProperty("authenticationSettings")
                                                   .Map(AuthenticationSettingsContract.Deserialize),
                Contact = jsonObject.TryGetJsonObjectProperty("contact")
                                    .Map(ApiContactInformation.Deserialize),
                Description = jsonObject.TryGetStringProperty("description"),
                DisplayName = jsonObject.TryGetStringProperty("displayName"),
                Format = jsonObject.TryGetProperty("format")
                                   .Map(ApiFormatOption.Deserialize),
                IsCurrent = jsonObject.TryGetBoolProperty("isCurrent"),
                License = jsonObject.TryGetJsonObjectProperty("license")
                                    .Map(ApiLicenseInformation.Deserialize),
                Path = jsonObject.TryGetStringProperty("path"),
                Protocols = jsonObject.TryGetJsonArrayProperty("protocols")
                                      .Map(jsonArray => jsonArray.Choose(node => node is null ? null : ProtocolOption.Deserialize(node))
                                                                 .ToArray()),
                ServiceUrl = jsonObject.TryGetStringProperty("serviceUrl"),
                SourceApiId = jsonObject.TryGetStringProperty("sourceApiId"),
                SubscriptionKeyParameterNames = jsonObject.TryGetJsonObjectProperty("subscriptionKeyParameterNames")
                                                          .Map(SubscriptionKeyParameterNamesContract.Deserialize),
                SubscriptionRequired = jsonObject.TryGetBoolProperty("subscriptionRequired"),
                TermsOfServiceUrl = jsonObject.TryGetStringProperty("termsOfServiceUrl"),
                Type = jsonObject.TryGetProperty("type")
                                 .Map(ApiTypeOption.Deserialize),
                Value = jsonObject.TryGetStringProperty("value"),
                WsdlSelector = jsonObject.TryGetJsonObjectProperty("wsdlSelector")
                                         .Map(ApiCreateOrUpdatePropertiesWsdlSelector.Deserialize)
            };

        public sealed record ApiTypeOption
        {
            private readonly string value;

            private ApiTypeOption(string value)
            {
                this.value = value;
            }

            public static ApiTypeOption GraphQl => new("graphql");
            public static ApiTypeOption Http => new("http");
            public static ApiTypeOption Soap => new("soap");
            public static ApiTypeOption WebSocket => new("websocket");

            public override string ToString() => value;

            public JsonNode Serialize() => JsonValue.Create(ToString()) ?? throw new JsonException("Value cannot be null.");

            public static ApiTypeOption Deserialize(JsonNode node) =>
                node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var value)
                    ? value switch
                    {
                        _ when nameof(GraphQl).Equals(value, StringComparison.OrdinalIgnoreCase) => GraphQl,
                        _ when nameof(Http).Equals(value, StringComparison.OrdinalIgnoreCase) => Http,
                        _ when nameof(Soap).Equals(value, StringComparison.OrdinalIgnoreCase) => Soap,
                        _ when nameof(WebSocket).Equals(value, StringComparison.OrdinalIgnoreCase) => WebSocket,
                        _ => throw new JsonException($"'{value}' is not a valid {nameof(ApiTypeOption)}.")
                    }
                        : throw new JsonException("Node must be a string JSON value.");
        }

        public sealed record ApiVersionSetContractDetails
        {
            public string? Description { get; init; }
            public string? Id { get; init; }
            public string? Name { get; init; }
            public string? VersionHeaderName { get; init; }
            public VersioningSchemeOption? VersioningScheme { get; init; }
            public string? VersionQueryName { get; init; }

            public JsonObject Serialize() =>
                new JsonObject()
                    .AddPropertyIfNotNull("description", Description)
                    .AddPropertyIfNotNull("id", Id)
                    .AddPropertyIfNotNull("name", Name)
                    .AddPropertyIfNotNull("versionHeaderName", VersionHeaderName)
                    .AddPropertyIfNotNull("apiType", VersioningScheme?.Serialize())
                    .AddPropertyIfNotNull("versionQueryName", VersionQueryName);

            public static ApiVersionSetContractDetails Deserialize(JsonObject jsonObject) =>
                new()
                {
                    Description = jsonObject.TryGetStringProperty("description"),
                    Id = jsonObject.TryGetStringProperty("id"),
                    Name = jsonObject.TryGetStringProperty("name"),
                    VersionHeaderName = jsonObject.TryGetStringProperty("versionHeaderName"),
                    VersioningScheme = jsonObject.TryGetProperty("apiType").Map(VersioningSchemeOption.Deserialize),
                    VersionQueryName = jsonObject.TryGetStringProperty("versionQueryName"),
                };

            public sealed record VersioningSchemeOption
            {
                private readonly string value;

                private VersioningSchemeOption(string value)
                {
                    this.value = value;
                }

                public static VersioningSchemeOption Header => new("Header");
                public static VersioningSchemeOption Query => new("Query");
                public static VersioningSchemeOption Segment => new("Segment");

                public override string ToString() => value;

                public JsonNode Serialize() => JsonValue.Create(ToString()) ?? throw new JsonException("Value cannot be null.");

                public static VersioningSchemeOption Deserialize(JsonNode node) =>
                    node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var value)
                        ? value switch
                        {
                            _ when nameof(Header).Equals(value, StringComparison.OrdinalIgnoreCase) => Header,
                            _ when nameof(Query).Equals(value, StringComparison.OrdinalIgnoreCase) => Query,
                            _ when nameof(Segment).Equals(value, StringComparison.OrdinalIgnoreCase) => Segment,
                            _ => throw new JsonException($"'{value}' is not a valid {nameof(VersioningSchemeOption)}.")
                        }
                        : throw new JsonException("Node must be a string JSON value.");
            }
        }

        public sealed record AuthenticationSettingsContract
        {
            public OAuth2AuthenticationSettingsContract? OAuth2 { get; init; }
            public OpenIdAuthenticationSettingsContract? OpenId { get; init; }

            public JsonObject Serialize() =>
                new JsonObject()
                    .AddPropertyIfNotNull("oAuth2", OAuth2?.Serialize())
                    .AddPropertyIfNotNull("openid", OpenId?.Serialize());

            public static AuthenticationSettingsContract Deserialize(JsonObject jsonObject) =>
                new()
                {
                    OAuth2 = jsonObject.TryGetJsonObjectProperty("oAuth2").Map(OAuth2AuthenticationSettingsContract.Deserialize),
                    OpenId = jsonObject.TryGetJsonObjectProperty("openid").Map(OpenIdAuthenticationSettingsContract.Deserialize),
                };

            public sealed record OAuth2AuthenticationSettingsContract
            {
                public string? AuthorizationServerId { get; init; }
                public string? Scope { get; init; }

                public JsonObject Serialize() =>
                    new JsonObject()
                        .AddPropertyIfNotNull("authorizationServerId", AuthorizationServerId)
                        .AddPropertyIfNotNull("scope", Scope);

                public static OAuth2AuthenticationSettingsContract Deserialize(JsonObject jsonObject) =>
                    new()
                    {
                        AuthorizationServerId = jsonObject.TryGetStringProperty("authorizationServerId"),
                        Scope = jsonObject.TryGetStringProperty("scope")
                    };
            }

            public sealed record OpenIdAuthenticationSettingsContract
            {
                public string[]? BearerTokenSendingMethods { get; init; }
                public string? OpenIdProviderId { get; init; }

                public JsonObject Serialize() =>
                    new JsonObject()
                        .AddPropertyIfNotNull("bearerTokenSendingMethods", BearerTokenSendingMethods?.Choose(method => (JsonNode?)method)
                                                                                                    ?.ToJsonArray())
                        .AddPropertyIfNotNull("openidProviderId", OpenIdProviderId);

                public static OpenIdAuthenticationSettingsContract Deserialize(JsonObject jsonObject) =>
                    new()
                    {
                        BearerTokenSendingMethods = jsonObject.TryGetJsonArrayProperty("bearerTokenSendingMethods")
                                                              .Map(jsonArray => jsonArray.Choose(node => node?.GetValue<string>())
                                                                                         .ToArray()),
                        OpenIdProviderId = jsonObject.TryGetStringProperty("openidProviderId")
                    };
            }
        }

        public sealed record ApiContactInformation
        {
            public string? Email { get; init; }
            public string? Name { get; init; }
            public string? Url { get; init; }

            public JsonObject Serialize() =>
                new JsonObject()
                    .AddPropertyIfNotNull("email", Email)
                    .AddPropertyIfNotNull("name", Name)
                    .AddPropertyIfNotNull("url", Url);

            public static ApiContactInformation Deserialize(JsonObject jsonObject) =>
                new()
                {
                    Email = jsonObject.TryGetStringProperty("email"),
                    Name = jsonObject.TryGetStringProperty("name"),
                    Url = jsonObject.TryGetStringProperty("url")
                };
        }

        public sealed record ApiFormatOption
        {
            private readonly string value;

            private ApiFormatOption(string value)
            {
                this.value = value;
            }

            public static ApiFormatOption GraphQl => new("graphql-link");
            public static ApiFormatOption OpenApi => new("openapi");
            public static ApiFormatOption OpenApiJson => new("openapi+json");
            public static ApiFormatOption OpenApiJsonLink => new("openapi+json-link");
            public static ApiFormatOption OpenApiLink => new("openapi-link");
            public static ApiFormatOption SwaggerJson => new("swagger-json");
            public static ApiFormatOption SwaggerLinkJson => new("swagger-link-json");
            public static ApiFormatOption WadlLinkJson => new("wadl-link-json");
            public static ApiFormatOption WadlXml => new("wadl-xml");
            public static ApiFormatOption Wsdl => new("wsdl");
            public static ApiFormatOption WsdlLink => new("wsdl-link");

            public override string ToString() => value;

            public JsonNode Serialize() => JsonValue.Create(ToString()) ?? throw new JsonException("Value cannot be null.");

            public static ApiFormatOption Deserialize(JsonNode node) =>
                node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var value)
                    ? value switch
                    {
                        _ when nameof(GraphQl).Equals(value, StringComparison.OrdinalIgnoreCase) => GraphQl,
                        _ when nameof(OpenApi).Equals(value, StringComparison.OrdinalIgnoreCase) => OpenApi,
                        _ when nameof(OpenApiJson).Equals(value, StringComparison.OrdinalIgnoreCase) => OpenApiJson,
                        _ when nameof(OpenApiJsonLink).Equals(value, StringComparison.OrdinalIgnoreCase) => OpenApiJsonLink,
                        _ when nameof(OpenApiLink).Equals(value, StringComparison.OrdinalIgnoreCase) => OpenApiLink,
                        _ when nameof(SwaggerJson).Equals(value, StringComparison.OrdinalIgnoreCase) => SwaggerJson,
                        _ when nameof(SwaggerLinkJson).Equals(value, StringComparison.OrdinalIgnoreCase) => SwaggerLinkJson,
                        _ when nameof(WadlLinkJson).Equals(value, StringComparison.OrdinalIgnoreCase) => WadlLinkJson,
                        _ when nameof(WadlXml).Equals(value, StringComparison.OrdinalIgnoreCase) => WadlXml,
                        _ when nameof(Wsdl).Equals(value, StringComparison.OrdinalIgnoreCase) => Wsdl,
                        _ when nameof(WsdlLink).Equals(value, StringComparison.OrdinalIgnoreCase) => WsdlLink,
                        _ => throw new JsonException($"'{value}' is not a valid {nameof(ApiFormatOption)}.")
                    }
                        : throw new JsonException("Node must be a string JSON value.");
        }

        public sealed record ApiLicenseInformation
        {
            public string? Name { get; init; }
            public string? Url { get; init; }

            public JsonObject Serialize() =>
                new JsonObject()
                    .AddPropertyIfNotNull("name", Name)
                    .AddPropertyIfNotNull("url", Url);

            public static ApiLicenseInformation Deserialize(JsonObject jsonObject) =>
                new()
                {
                    Name = jsonObject.TryGetStringProperty("name"),
                    Url = jsonObject.TryGetStringProperty("url")
                };
        }

        public sealed record ProtocolOption
        {
            private readonly string value;

            private ProtocolOption(string value)
            {
                this.value = value;
            }

            public static ProtocolOption Http => new("http");
            public static ProtocolOption Https => new("https");
            public static ProtocolOption Ws => new("ws");
            public static ProtocolOption Wss => new("wss");

            public override string ToString() => value;

            public JsonNode Serialize() => JsonValue.Create(ToString()) ?? throw new JsonException("Value cannot be null.");

            public static ProtocolOption Deserialize(JsonNode node) =>
                node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var value)
                    ? value switch
                    {
                        _ when nameof(Http).Equals(value, StringComparison.OrdinalIgnoreCase) => Http,
                        _ when nameof(Https).Equals(value, StringComparison.OrdinalIgnoreCase) => Https,
                        _ when nameof(Ws).Equals(value, StringComparison.OrdinalIgnoreCase) => Ws,
                        _ when nameof(Wss).Equals(value, StringComparison.OrdinalIgnoreCase) => Wss,
                        _ => throw new JsonException($"'{value}' is not a valid {nameof(ProtocolOption)}.")
                    }
                        : throw new JsonException("Node must be a string JSON value.");
        }

        public sealed record SubscriptionKeyParameterNamesContract
        {
            public string? Header { get; init; }
            public string? Query { get; init; }

            public JsonObject Serialize() =>
                new JsonObject()
                    .AddPropertyIfNotNull("header", Header)
                    .AddPropertyIfNotNull("query", Query);

            public static SubscriptionKeyParameterNamesContract Deserialize(JsonObject jsonObject) =>
                new()
                {
                    Header = jsonObject.TryGetStringProperty("header"),
                    Query = jsonObject.TryGetStringProperty("query")
                };
        }

        public sealed record ApiCreateOrUpdatePropertiesWsdlSelector
        {
            public string? WsdlEndpointName { get; init; }
            public string? WsdlServiceName { get; init; }

            public JsonObject Serialize() =>
                new JsonObject()
                    .AddPropertyIfNotNull("wsdlEndpointName", WsdlEndpointName)
                    .AddPropertyIfNotNull("wsdlServiceName", WsdlServiceName);

            public static ApiCreateOrUpdatePropertiesWsdlSelector Deserialize(JsonObject jsonObject) =>
                new()
                {
                    WsdlEndpointName = jsonObject.TryGetStringProperty("wsdlEndpointName"),
                    WsdlServiceName = jsonObject.TryGetStringProperty("wsdlServiceName")
                };
        }
    }

    public JsonObject Serialize() =>
        new JsonObject()
            .AddProperty("properties", Properties.Serialize());

    public static ApiModel Deserialize(ApiName name, JsonObject jsonObject) =>
        new()
        {
            Name = jsonObject.TryGetStringProperty("name") ?? name.ToString(),
            Properties = jsonObject.GetJsonObjectProperty("properties")
                                   .Map(ApiCreateOrUpdateProperties.Deserialize)!
        };
}