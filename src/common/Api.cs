using Azure.Core.Pipeline;
using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public sealed record ApiResource : IResourceWithReference
{
    private ApiResource() { }

    public string FileName { get; } = "apiInformation.json";

    public string CollectionDirectoryName { get; } = "apis";

    public string SingularName { get; } = "api";

    public string PluralName { get; } = "apis";

    public string CollectionUriPath { get; } = "apis";

    public Type DtoType { get; } = typeof(ApiDto);

    public ImmutableDictionary<IResource, string> OptionalReferencedResourceDtoProperties { get; } =
        ImmutableDictionary.Create<IResource, string>()
                           .Add(VersionSetResource.Instance, nameof(ApiDto.Properties.ApiVersionSetId));

    public static ApiResource Instance { get; } = new();
}

public sealed record ApiDto
{
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

        [JsonPropertyName("apiType")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? ApiType { get; init; }

        [JsonPropertyName("apiVersion")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? ApiVersion { get; init; }

        [JsonPropertyName("apiVersionDescription")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? ApiVersionDescription { get; init; }

        [JsonPropertyName("apiVersionSet")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public ApiVersionSetContractDetails? ApiVersionSet { get; init; }

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

        [JsonPropertyName("displayName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? DisplayName { get; init; }

        [JsonPropertyName("format")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Format { get; init; }

        [JsonPropertyName("isCurrent")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool? IsCurrent { get; init; }

        [JsonPropertyName("license")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public ApiLicenseInformation? License { get; init; }

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

        [JsonPropertyName("translateRequiredQueryParameters")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? TranslateRequiredQueryParameters { get; init; }

        [JsonPropertyName("type")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Type { get; init; }

        [JsonPropertyName("value")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Value { get; init; }

        [JsonPropertyName("wsdlSelector")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public WsdlSelectorContract? WsdlSelector { get; init; }

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
}

public static partial class ResourceModule
{
    private static async ValueTask PutApiInApim(ResourceName name, JsonObject dto, GetResourceDtoFromApim getApimDto, HttpPipeline pipeline, ServiceUri serviceUri, CancellationToken cancellationToken)
    {
        IResourceWithDto resource = ApiResource.Instance;
        ParentChain parentChain = ParentChain.Empty;

        var uri = resource.GetUri(name, parentChain, serviceUri);
        var formattedDto = await formatDto();

        var result = await pipeline.PutJson(uri, formattedDto, cancellationToken);        
        result.IfErrorThrow();

        // Non-current revisions are not allowed to update certain properties.
        // Replace them with the current revision's properties if necessary.
        async ValueTask<JsonObject> formatDto()
        {
            // If this is the current revision, return the DTO as-is.
            var rootName = ApiRevisionModule.GetRootName(name);
            if (name == rootName)
            {
                return dto;
            }

            // Otherwise, get the current revision's DTO from APIM...
            var existingDto = await getApimDto(resource, rootName, parentChain, cancellationToken);

            // ...and use its properties to format the new revision's DTO.
            var result = from existingDtoObject in JsonNodeModule.To<ApiDto>(existingDto, resource.SerializerOptions)
                         from newDtoObject in JsonNodeModule.To<ApiDto>(dto, resource.SerializerOptions)
                         let formattedDtoObject = newDtoObject with
                         {
                             Properties = newDtoObject.Properties with
                             {
                                 Type = existingDtoObject.Properties.Type,
                                 Description = existingDtoObject.Properties.Description,
                                 SubscriptionRequired = existingDtoObject.Properties.SubscriptionRequired,
                                 ApiVersion = existingDtoObject.Properties.ApiVersion,
                                 ApiRevisionDescription = existingDtoObject.Properties.ApiRevisionDescription,
                                 Path = existingDtoObject.Properties.Path,
                                 Protocols = existingDtoObject.Properties.Protocols
                             }
                         }
                         from updatedDto in JsonObjectModule.From(formattedDtoObject, resource.SerializerOptions)
                         select updatedDto;

            return result.IfErrorThrow();
        }
    }

    /// <summary>
    /// If the 'serviceUrl' property is empty, remove it. APIM doesn't support publishing blank service URLs.
    /// </summary>
    private static JsonObject FormatInformationFileDto(this ApiResource resource, JsonObject dtoJson)
    {
        var serializerOptions = ((IResourceWithDto)resource).SerializerOptions;

        var dto = JsonNodeModule.To<ApiDto>(dtoJson, serializerOptions)
                                .IfErrorThrow();

        dto = string.IsNullOrWhiteSpace(dto.Properties.ServiceUrl)
                ? dto with { Properties = dto.Properties with { ServiceUrl = null } }
                : dto;

        return JsonObjectModule.From(dto, serializerOptions)
                               .IfErrorThrow();
    }
}