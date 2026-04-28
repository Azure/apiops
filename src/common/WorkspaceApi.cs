using Azure.Core.Pipeline;
using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public sealed record WorkspaceApiResource : IResourceWithReference, IChildResource
{
    private WorkspaceApiResource() { }

    public string FileName { get; } = "apiInformation.json";

    public string CollectionDirectoryName { get; } = "apis";

    public string SingularName { get; } = "api";

    public string PluralName { get; } = "apis";

    public string CollectionUriPath { get; } = "apis";

    public Type DtoType { get; } = typeof(WorkspaceApiDto);

    public ImmutableDictionary<IResource, string> OptionalReferencedResourceDtoProperties { get; } =
        ImmutableDictionary.Create<IResource, string>()
                           .Add(WorkspaceVersionSetResource.Instance, nameof(WorkspaceApiDto.Properties.ApiVersionSetId));

    public IResource Parent { get; } = WorkspaceResource.Instance;

    public static WorkspaceApiResource Instance { get; } = new();
}

public sealed record WorkspaceApiDto
{
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public required WorkspaceApiCreateOrUpdateProperties Properties { get; init; }

    public sealed record WorkspaceApiCreateOrUpdateProperties
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
        public WorkspaceApiVersionSetContractDetails? ApiVersionSet { get; init; }

        [JsonPropertyName("apiVersionSetId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? ApiVersionSetId { get; init; }

        [JsonPropertyName("authenticationSettings")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public WorkspaceAuthenticationSettingsContract? AuthenticationSettings { get; init; }

        [JsonPropertyName("contact")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public WorkspaceApiContactInformation? Contact { get; init; }

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
        public WorkspaceApiLicenseInformation? License { get; init; }

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
        public WorkspaceSubscriptionKeyParameterNamesContract? SubscriptionKeyParameterNames { get; init; }

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
        public WorkspaceWsdlSelectorContract? WsdlSelector { get; init; }

        public sealed record WorkspaceAuthenticationSettingsContract
        {
            [JsonPropertyName("oAuth2")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
            public WorkspaceOAuth2AuthenticationSettingsContract? OAuth2 { get; init; }

            [JsonPropertyName("openid")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
            public WorkspaceOpenIdAuthenticationSettingsContract? OpenId { get; init; }
        }

        public sealed record WorkspaceOAuth2AuthenticationSettingsContract
        {
            [JsonPropertyName("authorizationServerId")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
            public string? AuthorizationServerId { get; init; }

            [JsonPropertyName("scope")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
            public string? Scope { get; init; }
        }

        public sealed record WorkspaceOpenIdAuthenticationSettingsContract
        {
            [JsonPropertyName("bearerTokenSendingMethods")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
            public ImmutableArray<string>? BearerTokenSendingMethods { get; init; }

            [JsonPropertyName("openidProviderId")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
            public string? OpenIdProviderId { get; init; }
        }

        public sealed record WorkspaceApiContactInformation
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

        public sealed record WorkspaceApiLicenseInformation
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

        public sealed record WorkspaceApiVersionSetContractDetails
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

        public sealed record WorkspaceSubscriptionKeyParameterNamesContract
        {
            [JsonPropertyName("header")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
            public string? Header { get; init; }

            [JsonPropertyName("query")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
            public string? Query { get; init; }
        }

        public sealed record WorkspaceWsdlSelectorContract
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
    private static JsonObject FormatInformationFileDto(this WorkspaceApiResource resource, JsonObject dtoJson)
    {
        var serializerOptions = ((IResourceWithDto)resource).SerializerOptions;

        var dto = JsonNodeModule.To<WorkspaceApiDto>(dtoJson, serializerOptions)
                                .IfErrorThrow();

        // If the 'serviceUrl' property is empty, remove it. APIM doesn't support publishing blank service URLs.
        dto = string.IsNullOrWhiteSpace(dto.Properties.ServiceUrl)
                ? dto with { Properties = dto.Properties with { ServiceUrl = null } }
                : dto;

        return JsonObjectModule.From(dto, serializerOptions)
                               .IfErrorThrow();
    }

    private static Option<(ResourceName Name, ParentChain Ancestors)> ParseSpecificationFile(this WorkspaceApiResource resource, FileInfo? file, ServiceDirectory serviceDirectory)
    {
        if (file is null)
        {
            return Option.None;
        }

        var specificationFileNames = specifications.Select(GetSpecificationFileName);
        if (specificationFileNames.Contains(file.Name) is false)
        {
            return Option.None;
        }

        return resource.ParseDirectory(file.Directory, serviceDirectory);
    }

    private static async ValueTask PutWorkspaceApiInApim(ResourceName name,
                                                         JsonObject dto,
                                                         ParentChain parents,
                                                         GetResourceDtoFromApim getApimDto,
                                                         HttpPipeline pipeline,
                                                         ServiceUri serviceUri,
                                                         CancellationToken cancellationToken)
    {
        IResourceWithDto resource = WorkspaceApiResource.Instance;

        var uri = resource.GetUri(name, parents, serviceUri);
        var formattedDto = await formatDto();

        var result = await pipeline.PutJson(uri, formattedDto, cancellationToken);
        result.IfErrorThrow();

        async ValueTask<JsonObject> formatDto()
        {
            var rootName = ApiRevisionModule.GetRootName(name);
            if (name == rootName)
            {
                return dto;
            }

            var existingDto = await getApimDto(resource, rootName, parents, cancellationToken);

            var workspaceDtoResult = from existingDtoObject in JsonNodeModule.To<WorkspaceApiDto>(existingDto, resource.SerializerOptions)
                                     from newDtoObject in JsonNodeModule.To<WorkspaceApiDto>(dto, resource.SerializerOptions)
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

            return workspaceDtoResult.IfErrorThrow();
        }
    }
}
