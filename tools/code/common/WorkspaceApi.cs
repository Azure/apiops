using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Flurl;
using LanguageExt;
using Polly;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using YamlDotNet.System.Text.Json;

namespace common;

public sealed record WorkspaceApisUri : ResourceUri
{
    public required WorkspaceUri Parent { get; init; }

    private static string PathSegment { get; } = "apis";

    protected override Uri Value =>
        Parent.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static WorkspaceApisUri From(WorkspaceName workspaceName, ManagementServiceUri serviceUri) =>
        new() { Parent = WorkspaceUri.From(workspaceName, serviceUri) };
}

public sealed record WorkspaceApiUri : ResourceUri
{
    public required WorkspaceApisUri Parent { get; init; }

    public required ApiName Name { get; init; }

    protected override Uri Value =>
        Parent.ToUri().AppendPathSegment(Name.ToString()).ToUri();

    public static WorkspaceApiUri From(ApiName name, WorkspaceName workspaceName, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = WorkspaceApisUri.From(workspaceName, serviceUri),
            Name = name
        };
}

public sealed record WorkspaceApisDirectory : ResourceDirectory
{
    public required WorkspaceDirectory Parent { get; init; }

    private static string Name { get; } = "apis";

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name);

    public static WorkspaceApisDirectory From(WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new() { Parent = WorkspaceDirectory.From(workspaceName, serviceDirectory) };

    public static Option<WorkspaceApisDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        directory is not null
        && directory.Name == Name
            ? from parent in WorkspaceDirectory.TryParse(directory.Parent, serviceDirectory)
              select new WorkspaceApisDirectory { Parent = parent }
            : Option<WorkspaceApisDirectory>.None;
}

public sealed record WorkspaceApiDirectory : ResourceDirectory
{
    public required WorkspaceApisDirectory Parent { get; init; }

    public required ApiName Name { get; init; }

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name.Value);

    public static WorkspaceApiDirectory From(ApiName name, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceApisDirectory.From(workspaceName, serviceDirectory),
            Name = name
        };

    public static Option<WorkspaceApiDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        directory is not null
            ? from parent in WorkspaceApisDirectory.TryParse(directory?.Parent, serviceDirectory)
              let name = ApiName.From(directory!.Name)
              select new WorkspaceApiDirectory
              {
                  Parent = parent,
                  Name = name
              }
            : Option<WorkspaceApiDirectory>.None;
}

public sealed record WorkspaceApiInformationFile : ResourceFile
{
    public required WorkspaceApiDirectory Parent { get; init; }

    public static string Name { get; } = "apiInformation.json";

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile(Name);

    public static WorkspaceApiInformationFile From(ApiName name, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceApiDirectory.From(name, workspaceName, serviceDirectory)
        };

    public static Option<WorkspaceApiInformationFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        file is not null &&
        file.Name == Name
            ? from parent in WorkspaceApiDirectory.TryParse(file.Directory, serviceDirectory)
              select new WorkspaceApiInformationFile
              {
                  Parent = parent
              }
            : Option<WorkspaceApiInformationFile>.None;
}

public sealed record WorkspaceApiDto
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
}

public static class WorkspaceApiModule
{
    public static async ValueTask DeleteAll(this WorkspaceApisUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await uri.ListNames(pipeline, cancellationToken)
                 .IterParallel(async name =>
                 {
                     var resourceUri = new WorkspaceApiUri { Parent = uri, Name = name };
                     await resourceUri.Delete(pipeline, cancellationToken);
                 }, cancellationToken);

    public static IAsyncEnumerable<ApiName> ListNames(this WorkspaceApisUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(ApiName.From);

    public static IAsyncEnumerable<(ApiName Name, WorkspaceApiDto Dto)> List(this WorkspaceApisUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        uri.ListNames(pipeline, cancellationToken)
           .SelectAwait(async name =>
           {
               var resourceUri = new WorkspaceApiUri { Parent = uri, Name = name };
               var dto = await resourceUri.GetDto(pipeline, cancellationToken);
               return (name, dto);
           });

    public static async ValueTask<WorkspaceApiDto> GetDto(this WorkspaceApiUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = await pipeline.GetContent(uri.ToUri(), cancellationToken);
        return content.ToObjectFromJson<WorkspaceApiDto>();
    }

    public static async ValueTask<Option<BinaryData>> TryGetSpecificationContents(this WorkspaceApiUri apiUri, ApiSpecification specification, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        if (specification is ApiSpecification.GraphQl)
        {
            return await apiUri.TryGetGraphQlSchema(pipeline, cancellationToken);
        }

        BinaryData? content;
        try
        {
            var exportUri = GetExportUri(apiUri, specification, includeLink: true);
            var downloadUri = await GetSpecificationDownloadUri(exportUri, pipeline, cancellationToken);

            var nonAuthenticatedHttpPipeline = HttpPipelineBuilder.Build(ClientOptions.Default);
            content = await nonAuthenticatedHttpPipeline.GetContent(downloadUri, cancellationToken);
        }
        // If we can't download the specification through the download link, get it directly.
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.InternalServerError)
        {
            // Don't export XML specifications, as the non-link exports cannot be reimported.
            if (specification is ApiSpecification.Wsdl or ApiSpecification.Wadl)
            {
                return Option<BinaryData>.None;
            }

            var exportUri = GetExportUri(apiUri, specification, includeLink: false);
            var json = await pipeline.GetJsonObject(exportUri, cancellationToken);
            var contentString = json.GetProperty("value") switch
            {
                JsonValue jsonValue => jsonValue.ToString(),
                var node => node.ToJsonString(JsonObjectExtensions.SerializerOptions)
            };
            content = BinaryData.FromString(contentString);
        }

        // APIM exports OpenApiV2 to JSON. Convert to YAML if needed.
        if (specification is ApiSpecification.OpenApi openApi && openApi.Format is OpenApiFormat.Yaml && openApi.Version is OpenApiVersion.V2)
        {
            var yaml = YamlConverter.SerializeJson(content.ToString());
            content = BinaryData.FromString(yaml);
        }

        return content;
    }

    private static Uri GetExportUri(WorkspaceApiUri apiUri, ApiSpecification specification, bool includeLink)
    {
        var format = GetExportFormat(specification, includeLink);

        return apiUri.ToUri()
                     .SetQueryParam("format", format)
                     .SetQueryParam("export", "true")
                     .SetQueryParam("api-version", "2022-09-01-preview")
                     .ToUri();
    }

    private static string GetExportFormat(ApiSpecification specification, bool includeLink)
    {
        var formatWithoutLink = specification switch
        {
            ApiSpecification.Wadl => "wadl",
            ApiSpecification.Wsdl => "wsdl",
            ApiSpecification.OpenApi openApiSpecification =>
                (openApiSpecification.Version, openApiSpecification.Format) switch
                {
                    (OpenApiVersion.V2, _) => "swagger",
                    (OpenApiVersion.V3, OpenApiFormat.Yaml) => "openapi",
                    (OpenApiVersion.V3, OpenApiFormat.Json) => "openapi+json",
                    _ => throw new NotSupportedException()
                },
            _ => throw new NotSupportedException()
        };

        return includeLink ? $"{formatWithoutLink}-link" : formatWithoutLink;
    }

    private static async ValueTask<Uri> GetSpecificationDownloadUri(Uri exportUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var json = await pipeline.GetJsonObject(exportUri, cancellationToken);

        return json.GetJsonObjectProperty("properties")
                   .GetJsonObjectProperty("value")
                   .GetAbsoluteUriProperty("link");
    }

    public static async ValueTask Delete(this WorkspaceApiUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await pipeline.DeleteResource(uri.ToUri(), waitForCompletion: true, cancellationToken);

    public static async ValueTask DeleteAllRevisions(this WorkspaceApiUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await pipeline.DeleteResource(uri.ToUri()
                                         .SetQueryParam("deleteRevisions", "true")
                                         .ToUri(), waitForCompletion: true, cancellationToken);

    public static async ValueTask PutDto(this WorkspaceApiUri uri, WorkspaceApiDto dto, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        if (dto.Properties.Format is null && dto.Properties.Value is null)
        {
            var content = BinaryData.FromObjectAsJson(dto);
            await pipeline.PutContent(uri.ToUri(), content, cancellationToken);
        }
        else
        {
            if (dto.Properties.Type is "soap")
            {
                await PutSoapApi(uri, dto, pipeline, cancellationToken);
            }
            else
            {
                await PutNonSoapApi(uri, dto, pipeline, cancellationToken);
            }
        }

        using var _ =
            await new ResiliencePipelineBuilder<Response>()
                .AddRetry(new()
                {
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    MaxRetryAttempts = 5,
                    ShouldHandle = new PredicateBuilder<Response>().HandleResult(CreationInProgress)
                })
                .Build()
                .ExecuteAsync(async cancellationToken =>
                {
                    using var request = pipeline.CreateRequest(uri.ToUri(), RequestMethod.Get);
                    return await pipeline.SendRequestAsync(request, cancellationToken);
                }, cancellationToken);
    }

    private static async ValueTask PutSoapApi(WorkspaceApiUri uri, WorkspaceApiDto dto, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        // Import API with specification
        var soapUri = uri.ToUri().SetQueryParam("import", "true").ToUri();
        var soapDto = new ApiDto
        {
            Properties = new ApiDto.ApiCreateOrUpdateProperties
            {
                Format = "wsdl",
                Value = dto.Properties.Value,
                ApiType = "soap",
                DisplayName = dto.Properties.DisplayName,
                Path = dto.Properties.Path,
                Protocols = dto.Properties.Protocols,
                ApiVersion = dto.Properties.ApiVersion,
                ApiVersionDescription = dto.Properties.ApiVersionDescription,
                ApiVersionSetId = dto.Properties.ApiVersionSetId
            }
        };
        await pipeline.PutContent(soapUri, BinaryData.FromObjectAsJson(soapDto), cancellationToken);

        // Put API again without specification
        var updatedDto = dto with { Properties = dto.Properties with { Format = null, Value = null } };
        // SOAP apis sometimes fail on put; retry if needed
        await soapApiResiliencePipeline.Value
                .ExecuteAsync(async cancellationToken => await pipeline.PutContent(uri.ToUri(), BinaryData.FromObjectAsJson(updatedDto), cancellationToken), cancellationToken);
    }

    private static readonly Lazy<ResiliencePipeline> soapApiResiliencePipeline = new(() =>
        new ResiliencePipelineBuilder()
                    .AddRetry(new()
                    {
                        BackoffType = DelayBackoffType.Exponential,
                        UseJitter = true,
                        MaxRetryAttempts = 3,
                        ShouldHandle = new PredicateBuilder().Handle<HttpRequestException>(exception => exception.StatusCode == HttpStatusCode.Conflict && exception.Message.Contains("IdentifierAlreadyInUse", StringComparison.OrdinalIgnoreCase))
                    })
                    .Build());

    private static async ValueTask PutNonSoapApi(WorkspaceApiUri uri, WorkspaceApiDto dto, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        // Put API without the specification.
        var modelWithoutSpecification = dto with
        {
            Properties = dto.Properties with
            {
                Format = null,
                Value = null,
                ServiceUrl = null,
                Type = dto.Properties.Type,
                ApiType = dto.Properties.ApiType
            }
        };
        await pipeline.PutContent(uri.ToUri(), BinaryData.FromObjectAsJson(modelWithoutSpecification), cancellationToken);

        // Put API again with specification
        await pipeline.PutContent(uri.ToUri(), BinaryData.FromObjectAsJson(dto), cancellationToken);
    }

    private static bool CreationInProgress(Response response)
    {
        if (response.Status != (int)HttpStatusCode.Created)
        {
            return false;
        }

        if (response.Headers.Any(header => header.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
                                            && header.Value.Contains("application/json", StringComparison.OrdinalIgnoreCase)) is false)
        {
            return false;
        }

        try
        {
            return response.Content.ToObjectFromJson<JsonObject>()
                                   .TryGetJsonObjectProperty("properties")
                                   .Bind(json => json.TryGetStringProperty("ProvisioningState"))
                                   .ToOption()
                                   .Where(state => state.Equals("InProgress", StringComparison.OrdinalIgnoreCase))
                                   .IsSome;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static async ValueTask PutGraphQlSchema(this WorkspaceApiUri uri, BinaryData schema, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var contents = BinaryData.FromObjectAsJson(new JsonObject()
        {
            ["properties"] = new JsonObject
            {
                ["contentType"] = "application/vnd.ms-azure-apim.graphql.schema",
                ["document"] = new JsonObject()
                {
                    ["value"] = schema.ToString()
                }
            }
        });

        await pipeline.PutContent(uri.ToUri()
                                     .AppendPathSegment("schemas")
                                     .AppendPathSegment("graphql")
                                     .ToUri(), contents, cancellationToken);
    }

    public static async ValueTask<Option<BinaryData>> TryGetGraphQlSchema(this WorkspaceApiUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var schemaUri = uri.ToUri()
                           .AppendPathSegment("schemas")
                           .AppendPathSegment("graphql")
                           .ToUri();

        var schemaJsonOption = await pipeline.GetJsonObjectOption(schemaUri, cancellationToken);

        return schemaJsonOption.Map(GetGraphQlSpecificationFromSchemaResponse);
    }

    private static BinaryData GetGraphQlSpecificationFromSchemaResponse(JsonObject responseJson)
    {
        var schema = responseJson.GetJsonObjectProperty("properties")
                                 .GetJsonObjectProperty("document")
                                 .GetNonEmptyOrWhiteSpaceStringProperty("value");

        return BinaryData.FromString(schema);
    }

    public static IEnumerable<WorkspaceApiDirectory> ListDirectories(ManagementServiceDirectory serviceDirectory) =>
        from workspaceDirectory in WorkspaceModule.ListDirectories(serviceDirectory)
        let workspaceApisDirectory = new WorkspaceApisDirectory { Parent = workspaceDirectory }
        where workspaceApisDirectory.ToDirectoryInfo().Exists()
        from workspaceApiDirectoryInfo in workspaceApisDirectory.ToDirectoryInfo().ListDirectories("*")
        let name = ApiName.From(workspaceApiDirectoryInfo.Name)
        select new WorkspaceApiDirectory
        {
            Parent = workspaceApisDirectory,
            Name = name
        };

    public static IEnumerable<WorkspaceApiInformationFile> ListInformationFiles(ManagementServiceDirectory serviceDirectory) =>
        from workspaceApiDirectory in ListDirectories(serviceDirectory)
        let informationFile = new WorkspaceApiInformationFile { Parent = workspaceApiDirectory }
        where informationFile.ToFileInfo().Exists()
        select informationFile;

    public static async ValueTask WriteDto(this WorkspaceApiInformationFile file, WorkspaceApiDto dto, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto, JsonObjectExtensions.SerializerOptions);
        await file.ToFileInfo().OverwriteWithBinaryData(content, cancellationToken);
    }

    public static async ValueTask WriteSpecification(this WorkspaceApiSpecificationFile file, BinaryData contents, CancellationToken cancellationToken) =>
        await file.ToFileInfo().OverwriteWithBinaryData(contents, cancellationToken);

    public static async ValueTask<WorkspaceApiDto> ReadDto(this WorkspaceApiInformationFile file, CancellationToken cancellationToken)
    {
        var content = await file.ToFileInfo().ReadAsBinaryData(cancellationToken);
        return content.ToObjectFromJson<WorkspaceApiDto>();
    }
}