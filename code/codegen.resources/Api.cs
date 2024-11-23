namespace codegen.resources;

internal sealed record Api : IResourceWithName, IResourceWithInformationFile
{
    public static Api Instance = new();

    public string InformationFileType { get; } = "ApiInformationFile";

    public string InformationFileName { get; } = "apiInformation.json";

    public string DtoType { get; } = "ApiDto";

    public string CollectionDirectoryType { get; } = "ApisDirectory";

    public string CollectionDirectoryName { get; } = "apis";

    public string DirectoryType { get; } = "ApiDirectory";

    public string NameType { get; } = "ApiName";

    public string NameParameter { get; } = "apiName";

    public string SingularDescription { get; } = "Api";

    public string PluralDescription { get; } = "Apis";

    public string LoggerSingularDescription { get; } = "API";

    public string LoggerPluralDescription { get; } = "apis";

    public string CollectionUriType { get; } = "ApisUri";

    public string CollectionUriPath { get; } = "apis";

    public string UriType { get; } = "ApiUri";

    public string DtoCode { get; } =
"""
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public required ApiCreateOrUpdateProperties Properties { get; init; }

    public record ApiCreateOrUpdateProperties
    {
        [JsonPropertyName("path")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Path { get; init; }

        [JsonPropertyName("apiRevision")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? ApiRevision { get; init; }

        [JsonPropertyName("apiRevisionDescription")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? ApiRevisionDescription { get; init; }

        [JsonPropertyName("apiVersion")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? ApiVersion { get; init; }

        [JsonPropertyName("apiVersionDescription")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? ApiVersionDescription { get; init; }

        [JsonPropertyName("apiVersionSetId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? ApiVersionSetId { get; init; }

        [JsonPropertyName("authenticationSettings")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public AuthenticationSettingsContract? AuthenticationSettings { get; init; }

        [JsonPropertyName("contact")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public ApiContactInformation? Contact { get; init; }

        [JsonPropertyName("description")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Description { get; init; }

        [JsonPropertyName("isCurrent")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool? IsCurrent { get; init; }

        [JsonPropertyName("license")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public ApiLicenseInformation? License { get; init; }

        [JsonPropertyName("apiType")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? ApiType { get; init; }

        [JsonPropertyName("apiVersionSet")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public ApiVersionSetContractDetails? ApiVersionSet { get; init; }

        [JsonPropertyName("displayName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? DisplayName { get; init; }

        [JsonPropertyName("format")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Format { get; init; }

        [JsonPropertyName("protocols")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public ImmutableArray<string>? Protocols { get; init; }

        [JsonPropertyName("serviceUrl")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    #pragma warning disable CA1056 // URI-like properties should not be strings
        public string? ServiceUrl { get; init; }
    #pragma warning restore CA1056 // URI-like properties should not be strings

        [JsonPropertyName("sourceApiId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? SourceApiId { get; init; }

        [JsonPropertyName("translateRequiredQueryParameters")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? TranslateRequiredQueryParameters { get; init; }

        [JsonPropertyName("value")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Value { get; init; }

        [JsonPropertyName("wsdlSelector")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public WsdlSelectorContract? WsdlSelector { get; init; }

        [JsonPropertyName("subscriptionKeyParameterNames")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public SubscriptionKeyParameterNamesContract? SubscriptionKeyParameterNames { get; init; }

        [JsonPropertyName("subscriptionRequired")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool? SubscriptionRequired { get; init; }

        [JsonPropertyName("termsOfServiceUrl")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    #pragma warning disable CA1056 // URI-like properties should not be strings
        public string? TermsOfServiceUrl { get; init; }
    #pragma warning restore CA1056 // URI-like properties should not be strings

        [JsonPropertyName("type")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Type { get; init; }

        public record AuthenticationSettingsContract
        {
            [JsonPropertyName("oAuth2")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
            public OAuth2AuthenticationSettingsContract? OAuth2 { get; init; }

            [JsonPropertyName("openid")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
            public OpenIdAuthenticationSettingsContract? OpenId { get; init; }
        }

        public record OAuth2AuthenticationSettingsContract
        {
            [JsonPropertyName("authorizationServerId")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
            public string? AuthorizationServerId { get; init; }

            [JsonPropertyName("scope")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
            public string? Scope { get; init; }
        }

        public record OpenIdAuthenticationSettingsContract
        {
            [JsonPropertyName("bearerTokenSendingMethods")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
            public ImmutableArray<string>? BearerTokenSendingMethods { get; init; }

            [JsonPropertyName("openidProviderId")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
            public string? OpenIdProviderId { get; init; }
        }

        public record ApiContactInformation
        {
            [JsonPropertyName("email")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
            public string? Email { get; init; }

            [JsonPropertyName("name")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
            public string? Name { get; init; }

            [JsonPropertyName("url")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    #pragma warning disable CA1056 // URI-like properties should not be strings
            public string? Url { get; init; }
    #pragma warning restore CA1056 // URI-like properties should not be strings
        }

        public record ApiLicenseInformation
        {
            [JsonPropertyName("name")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
            public string? Name { get; init; }

            [JsonPropertyName("url")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    #pragma warning disable CA1056 // URI-like properties should not be strings
            public string? Url { get; init; }
    #pragma warning restore CA1056 // URI-like properties should not be strings
        }

        public record ApiVersionSetContractDetails
        {
            [JsonPropertyName("description")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
            public string? Description { get; init; }

            [JsonPropertyName("id")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
            public string? Id { get; init; }

            [JsonPropertyName("name")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
            public string? Name { get; init; }

            [JsonPropertyName("versionHeaderName")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
            public string? VersionHeaderName { get; init; }

            [JsonPropertyName("versionQueryName")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
            public string? VersionQueryName { get; init; }

            [JsonPropertyName("versioningScheme")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
            public string? VersioningScheme { get; init; }
        }

        public record SubscriptionKeyParameterNamesContract
        {
            [JsonPropertyName("header")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
            public string? Header { get; init; }

            [JsonPropertyName("query")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
            public string? Query { get; init; }
        }

        public record WsdlSelectorContract
        {
            [JsonPropertyName("wsdlEndpointName")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
            public string? WsdlEndpointName { get; init; }

            [JsonPropertyName("wsdlServiceName")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
            public string? WsdlServiceName { get; init; }
        }
    }
""";
}