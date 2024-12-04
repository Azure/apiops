using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Flurl;
using LanguageExt;
using Polly;
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using YamlDotNet.System.Text.Json;

namespace common;

public static partial class ApiModule
{
    public static async ValueTask<Option<BinaryData>> GetSpecificationContents(this ApiUri apiUri, ApiSpecification specification, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        switch (specification)
        {
            case ApiSpecification.GraphQl:
                return await apiUri.TryGetGraphQlSchema(pipeline, cancellationToken);
            default:
                var content = Option<BinaryData>.None;

                try
                {
                    content = await apiUri.GetSpecificationContentsThroughDownloadUri(specification, pipeline, cancellationToken);
                }
                // If we can't download the specification through the download link, get it directly.
                catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.InternalServerError)
                {
                    content = await apiUri.GetSpecificationContentsWithoutDownloadUri(specification, pipeline, cancellationToken);
                }

                return content.Map(ConvertJsonToYaml);
        }
    }

    private static async ValueTask<BinaryData> GetSpecificationContentsThroughDownloadUri(this ApiUri apiUri, ApiSpecification specification, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var exportUri = GetExportUri(apiUri, specification, includeLink: true);
        var downloadUri = await GetSpecificationDownloadUri(exportUri, pipeline, cancellationToken);

        var nonAuthenticatedHttpPipeline = HttpPipelineBuilder.Build(ClientOptions.Default);
        return await nonAuthenticatedHttpPipeline.GetContent(downloadUri, cancellationToken);
    }

    private static Uri GetExportUri(ApiUri apiUri, ApiSpecification specification, bool includeLink)
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

        var result = from value in json.GetJsonObjectProperty("value")
                     from link in value.GetAbsoluteUriProperty("link")
                     select link;

        return result.ThrowIfFail();
    }

    private static async ValueTask<Option<BinaryData>> GetSpecificationContentsWithoutDownloadUri(this ApiUri apiUri, ApiSpecification specification, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        switch (specification)
        {
            // Don't export XML specifications, as the non-link exports cannot be reimported.
            case ApiSpecification.Wsdl or ApiSpecification.Wadl:
                return Option<BinaryData>.None;
            default:
                var exportUri = GetExportUri(apiUri, specification, includeLink: false);
                var json = await pipeline.GetJsonObject(exportUri, cancellationToken);

                var contentResult = from property in json.GetProperty("value")
                                    let content = property switch
                                    {
                                        JsonValue => property.ToString(),
                                        _ => property.ToJsonString(JsonSerializerOptions.Web)
                                    }
                                    select BinaryData.FromString(content);

                return contentResult.ThrowIfFail();
        }
    }

    private static BinaryData ConvertJsonToYaml(BinaryData json)
    {
        var yaml = YamlConverter.SerializeJson(json.ToString(), jsonSerializerOptions: JsonSerializerOptions.Web);
        return BinaryData.FromString(yaml);
    }

    public static async ValueTask DeleteAllRevisions(this ApiUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await pipeline.DeleteResource(uri.ToUri()
                                         .SetQueryParam("deleteRevisions", "true")
                                         .ToUri(), waitForCompletion: true, cancellationToken);

    public static async ValueTask PutDto(this ApiUri uri, ApiDto dto, HttpPipeline pipeline, CancellationToken cancellationToken)
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

    private static async ValueTask PutSoapApi(ApiUri uri, ApiDto dto, HttpPipeline pipeline, CancellationToken cancellationToken)
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

    private static async ValueTask PutNonSoapApi(ApiUri uri, ApiDto dto, HttpPipeline pipeline, CancellationToken cancellationToken)
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
                                            && header.Value.Contains("application/json", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                return response.Content
                               .ToObjectFromJson<JsonObject>()
                               .GetJsonObjectProperty("properties")
                               .Bind(json => json.GetStringProperty("ProvisioningState"))
                               .Match(state => state.Equals("InProgress", StringComparison.OrdinalIgnoreCase),
                                      _ => false);
            }
            catch (JsonException)
            {
                return false;
            }
        }
        else
        {
            return false;
        }
    }

    public static async ValueTask PutGraphQlSchema(this ApiUri uri, BinaryData schema, HttpPipeline pipeline, CancellationToken cancellationToken)
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

    public static async ValueTask<Option<BinaryData>> TryGetGraphQlSchema(this ApiUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var schemaUri = uri.ToUri()
                           .AppendPathSegment("schemas")
                           .AppendPathSegment("graphql")
                           .ToUri();

        var schemaJsonOption = await pipeline.GetJsonObjectOption(schemaUri, cancellationToken);

        return schemaJsonOption.Map(GetGraphQlSpecificationFromSchemaResponse);
    }

    private static BinaryData GetGraphQlSpecificationFromSchemaResponse(JsonObject responseJson) =>
        responseJson.GetJsonObjectProperty("properties")
                    .Bind(json => json.GetJsonObjectProperty("document"))
                    .Bind(json => json.GetStringProperty("value"))
                    .Map(BinaryData.FromString)
                    .ThrowIfFail();
}
