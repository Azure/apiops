using System.Text.Json.Serialization;

namespace common.Models;

public sealed record Api([property: JsonPropertyName("name")] string Name,
                         [property: JsonPropertyName("properties")] Api.ApiCreateOrUpdateProperties Properties)
{
    public sealed record ApiCreateOrUpdateProperties([property: JsonPropertyName("path")] string Path,
                                                     [property: JsonPropertyName("displayName")] string DisplayName)
    {
        [JsonPropertyName("apiRevision")]
        public string? ApiRevision { get; init; }

        [JsonPropertyName("apiRevisionDescription")]
        public string? ApiRevisionDescription { get; init; }

        [JsonPropertyName("apiType")]
        public string? ApiType { get; init; }

        [JsonPropertyName("apiVersion")]
        public string? ApiVersion { get; init; }

        [JsonPropertyName("apiVersionDescription")]
        public string? ApiVersionDescription { get; init; }

        [JsonPropertyName("apiVersionSet")]
        public ApiVersionSetContractDetails? ApiVersionSet { get; init; }

        [JsonPropertyName("apiVersionSetId")]
        public string? ApiVersionSetId { get; init; }

        [JsonPropertyName("authenticationSettings")]
        public AuthenticationSettingsContract? AuthenticationSettings { get; init; }

        [JsonPropertyName("contact")]
        public ApiContactInformation? Contact { get; init; }

        [JsonPropertyName("description")]
        public string? Description { get; init; }

        [JsonPropertyName("format")]
        public string? Format { get; init; }

        [JsonPropertyName("isCurrent")]
        public bool? IsCurrent { get; init; }

        [JsonPropertyName("license")]
        public ApiLicenseInformation? License { get; init; }

        [JsonPropertyName("protocols")]
        public string[]? Protocols { get; init; }

        [JsonPropertyName("serviceUrl")]
        public string? ServiceUrl { get; init; }

        [JsonPropertyName("sourceApiId")]
        public string? SourceApiId { get; init; }

        [JsonPropertyName("subscriptionKeyParameterNames")]
        public SubscriptionKeyParameterNamesContract? SubscriptionKeyParameterNames { get; init; }

        [JsonPropertyName("subscriptionRequired")]
        public bool? SubscriptionRequired { get; init; }

        [JsonPropertyName("termsOfServiceUrl")]
        public string? TermsOfServiceUrl { get; init; }

        [JsonPropertyName("type")]
        public string? Type { get; init; }

        [JsonPropertyName("value")]
        public string? Value { get; init; }

        [JsonPropertyName("wsdlSelector")]
        public ApiCreateOrUpdatePropertiesWsdlSelector? WsdlSelector { get; init; }
    }

    public sealed record ApiVersionSetContractDetails
    {
        [JsonPropertyName("description")]
        public string? Description { get; init; }

        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("versionHeaderName")]
        public string? VersionHeaderName { get; init; }

        [JsonPropertyName("versioningScheme")]
        public string? VersioningScheme { get; init; }

        [JsonPropertyName("versionQueryName")]
        public string? VersionQueryName { get; init; }
    }

    public sealed record AuthenticationSettingsContract
    {
        [JsonPropertyName("oAuth2")]
        public OAuth2AuthenticationSettingsContract? OAuth2 { get; init; }

        [JsonPropertyName("openid")]
        public OpenIdAuthenticationSettingsContract? Openid { get; init; }
    }

    public sealed record OAuth2AuthenticationSettingsContract
    {
        [JsonPropertyName("authorizationServerId")]
        public string? AuthorizationServerId { get; init; }

        [JsonPropertyName("scope")]
        public string? Scope { get; init; }
    }

    public sealed record OpenIdAuthenticationSettingsContract
    {
        [JsonPropertyName("bearerTokenSendingMethods")]
        public string[]? BearerTokenSendingMethods { get; init; }

        [JsonPropertyName("openidProviderId")]
        public string? OpenidProviderId { get; init; }
    }

    public sealed record ApiContactInformation
    {
        [JsonPropertyName("name")]
        public string? Email { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("url")]
        public string? Url { get; init; }
    }

    public sealed record ApiLicenseInformation
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("url")]
        public string? Url { get; init; }
    }

    public sealed record SubscriptionKeyParameterNamesContract
    {
        [JsonPropertyName("header")]
        public string? Header { get; init; }

        [JsonPropertyName("query")]
        public string? Query { get; init; }
    }

    public sealed record ApiCreateOrUpdatePropertiesWsdlSelector
    {
        [JsonPropertyName("wsdlEndpointName")]
        public string? WsdlEndpointName { get; init; }

        [JsonPropertyName("wsdlServiceName")]
        public string? WsdlServiceName { get; init; }
    }
}