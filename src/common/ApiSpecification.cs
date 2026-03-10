using Azure.Core;
using Azure.Core.Pipeline;
using Flurl;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;
using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public delegate ValueTask<Option<(ApiSpecification Specification, BinaryData Contents)>> GetApiSpecificationFromApim(ResourceKey resourceKey, JsonObject dto, CancellationToken cancellationToken);
public delegate ValueTask<Option<(ApiSpecification Specification, BinaryData Contents)>> GetApiSpecificationFromFile(ResourceKey resourceKey, ReadFile readFile, CancellationToken cancellationToken);
public delegate ValueTask WriteApiSpecificationFile(ResourceKey resourceKey, ApiSpecification specification, BinaryData contents, CancellationToken cancellationToken);
public delegate ValueTask PutApiSpecificationInApim(ResourceKey resourceKey, ApiSpecification specification, BinaryData contents, CancellationToken cancellationToken);

public abstract record ApiSpecification
{
    public sealed record GraphQl : ApiSpecification
    {
        private GraphQl() { }

        public static GraphQl Instance { get; } = new();
    }

    public sealed record Wadl : ApiSpecification
    {
        private Wadl() { }

        public static Wadl Instance { get; } = new();
    }

    public sealed record Wsdl : ApiSpecification
    {
        private Wsdl() { }

        public static Wsdl Instance { get; } = new();
    }

    public sealed record OpenApi : ApiSpecification
    {
        public required OpenApiFormat Format { get; init; }
        public required OpenApiVersion Version { get; init; }
    }
}

public abstract record OpenApiFormat
{
    public sealed record Json : OpenApiFormat
    {
        private Json() { }

        public static Json Instance { get; } = new();
    }

    public sealed record Yaml : OpenApiFormat
    {
        private Yaml() { }

        public static Yaml Instance { get; } = new();
    }
}

public abstract record OpenApiVersion
{
    public sealed record V2 : OpenApiVersion
    {
        private V2() { }

        public static V2 Instance { get; } = new();
    }

    public sealed record V3 : OpenApiVersion
    {
        private V3() { }

        public static V3 Instance { get; } = new();
    }
}

public static partial class ResourceModule
{
    private static readonly ImmutableArray<ApiSpecification> specifications = [
            ApiSpecification.GraphQl.Instance,
            ApiSpecification.Wsdl.Instance,
            ApiSpecification.Wadl.Instance,
            new ApiSpecification.OpenApi { Format = OpenApiFormat.Json.Instance, Version = OpenApiVersion.V2.Instance },
            new ApiSpecification.OpenApi { Format = OpenApiFormat.Yaml.Instance, Version = OpenApiVersion.V2.Instance },
            new ApiSpecification.OpenApi { Format = OpenApiFormat.Json.Instance, Version = OpenApiVersion.V3.Instance },
            new ApiSpecification.OpenApi { Format = OpenApiFormat.Yaml.Instance, Version = OpenApiVersion.V3.Instance }
        ];

    public static void ConfigureGetApiSpecificationFromApim(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);
        ConfigureGetOptionalResourceDtoFromApim(builder);

        builder.TryAddSingleton(ResolveGetApiSpecificationFromApim);
    }

    private static GetApiSpecificationFromApim ResolveGetApiSpecificationFromApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ServiceUri>();
        var getOptionalDto = provider.GetRequiredService<GetOptionalResourceDtoFromApim>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var configuration = provider.GetRequiredService<IConfiguration>();

        var throttler = new ConcurrentDictionary<string, SemaphoreSlim>();

        return async (resourceKey, dto, cancellationToken) =>
        {
            var (resource, name, parents) = (resourceKey.Resource, resourceKey.Name, resourceKey.Parents);

            if (resource is not ApiResource && resource is not WorkspaceApiResource)
            {
                return Option.None;
            }

            var specificationOption = getSpecification(resource, dto);

            return await specificationOption.BindTask(async specification =>
            {
                // APIM has an issue where concurrent requests with the same version set always return the same specification.
                // If apiA and apiB share a version set, and we request their specification simultaneously, the same specification
                // file is returned (even if they have different specifications).
                // We throttle concurrent requests with the same version set ID to avoid this issue.
                var versionSetIdOption =
                    dto.GetJsonObjectProperty("properties")
                       .Bind(properties => properties.GetStringProperty("apiVersionSetId"))
                       .Map(ResourceModule.SetAbsoluteToRelativeId)
                       .ToOption();

                var throttlerKeyOption = versionSetIdOption;

                // APIM has an issue where concurrent requests with the same root API always return the current revision's specification.
                // If we request the specification for apiA (current) and apiA;rev=3 (non-current) simultaneously, apiA's specification
                // is returned in both instances. We throttle concurrent requests with the same root name to avoid this issue.
                var throttlerKey = throttlerKeyOption.IfNone(() => ApiRevisionModule.GetRootName(name).ToString());

                var semaphore = throttler.GetOrAdd(throttlerKey, _ => new SemaphoreSlim(1, 1));

                await semaphore.WaitAsync(cancellationToken);

                try
                {
                    return from contents in await getSpecificationContents(resourceKey, specification, cancellationToken)
                           select (specification, contents);
                }
                finally
                {
                    semaphore.Release();
                }
            });
        };

        Option<ApiSpecification> getSpecification(IResource resource, JsonObject dtoJson)
        {
            var apiType = string.Empty;
            var serializerOptions = ((IResourceWithDto)resource).SerializerOptions;

            switch (resource)
            {
                case ApiResource:
                    apiType = JsonNodeModule.To<ApiDto>(dtoJson, serializerOptions)
                                            .IfErrorThrow()
                                            .Properties.Type;
                    break;
                case WorkspaceApiResource:
                    apiType = JsonNodeModule.To<WorkspaceApiDto>(dtoJson, serializerOptions)
                                            .IfErrorThrow()
                                            .Properties.Type;
                    break;
            }

            return apiType switch
            {
                null => Option.Some(getDefaultSpecification()),
                var value when "http".Equals(value, StringComparison.OrdinalIgnoreCase) => Option.Some(getDefaultSpecification()),
                var value when "graphql".Equals(value, StringComparison.OrdinalIgnoreCase) => Option<ApiSpecification>.Some(ApiSpecification.GraphQl.Instance),
                var value when "soap".Equals(value, StringComparison.OrdinalIgnoreCase) => Option<ApiSpecification>.Some(ApiSpecification.Wsdl.Instance),
                _ => Option.None
            };
        }

        ApiSpecification getDefaultSpecification() =>
            configuration.GetValue("API_SPECIFICATION_FORMAT")
                         .IfNone(() => configuration.GetValue("apiSpecificationFormat"))
                         .Map(format => format switch
                         {
                             var value when "Wadl".Equals(value, StringComparison.OrdinalIgnoreCase) =>
                                ApiSpecification.Wadl.Instance as ApiSpecification,
                             var value when "JSON".Equals(value, StringComparison.OrdinalIgnoreCase) =>
                                new ApiSpecification.OpenApi
                                {
                                    Format = OpenApiFormat.Json.Instance,
                                    Version = OpenApiVersion.V3.Instance
                                },
                             var value when "YAML".Equals(value, StringComparison.OrdinalIgnoreCase) =>
                                new ApiSpecification.OpenApi
                                {
                                    Format = OpenApiFormat.Yaml.Instance,
                                    Version = OpenApiVersion.V3.Instance
                                },
                             var value when "OpenApiV2Json".Equals(value, StringComparison.OrdinalIgnoreCase) =>
                                new ApiSpecification.OpenApi
                                {
                                    Format = OpenApiFormat.Json.Instance,
                                    Version = OpenApiVersion.V2.Instance
                                },
                             var value when "OpenApiV2Yaml".Equals(value, StringComparison.OrdinalIgnoreCase) =>
                                new ApiSpecification.OpenApi
                                {
                                    Format = OpenApiFormat.Yaml.Instance,
                                    Version = OpenApiVersion.V2.Instance
                                },
                             var value when "OpenApiV3Json".Equals(value, StringComparison.OrdinalIgnoreCase) =>
                                new ApiSpecification.OpenApi
                                {
                                    Format = OpenApiFormat.Json.Instance,
                                    Version = OpenApiVersion.V3.Instance
                                },
                             var value when "OpenApiV3Yaml".Equals(value, StringComparison.OrdinalIgnoreCase) =>
                                new ApiSpecification.OpenApi
                                {
                                    Format = OpenApiFormat.Yaml.Instance,
                                    Version = OpenApiVersion.V3.Instance
                                },
                             var value =>
                                throw new NotSupportedException($"API specification format '{value}' defined in configuration is not supported.")

                         })
                        .IfNone(() => new ApiSpecification.OpenApi
                        {
                            Format = OpenApiFormat.Yaml.Instance,
                            Version = OpenApiVersion.V3.Instance
                        });

        async ValueTask<Option<BinaryData>> getSpecificationContents(ResourceKey resourceKey, ApiSpecification specification, CancellationToken cancellationToken)
        {
            var (resource, name, parents) = (resourceKey.Resource, resourceKey.Name, resourceKey.Parents);

            switch (specification)
            {
                case ApiSpecification.GraphQl:
                    return await getGraphQlSpecificationContents(resourceKey, cancellationToken);
                default:
                    // Get a link to download the specification
                    var exportUri = resource.GetUri(name, parents, serviceUri)
                                            .SetQueryParam("format", specification switch
                                            {
                                                ApiSpecification.Wsdl => "wsdl-link",
                                                ApiSpecification.Wadl => "wadl-link",
                                                ApiSpecification.OpenApi openApi when openApi.Version is OpenApiVersion.V2 => "swagger-link",
                                                ApiSpecification.OpenApi openApi when openApi.Format is OpenApiFormat.Json => "openapi+json-link",
                                                ApiSpecification.OpenApi openApi when openApi.Format is OpenApiFormat.Yaml => "openapi-link",
                                                _ => throw new InvalidOperationException($"Specification {specification} is not supported.")
                                            })
                                            .SetQueryParam("export", true)
                                            .ToUri();

                    var downloadUriResult = from exportResult in await pipeline.GetContent(exportUri, cancellationToken)
                                            from exportJson in JsonObjectModule.From(exportResult)
                                            from value in resource switch
                                            {
                                                ApiResource => exportJson.GetJsonObjectProperty("value"),
                                                WorkspaceApiResource => from properties in exportJson.GetJsonObjectProperty("properties")
                                                                        from value in properties.GetJsonObjectProperty("value")
                                                                        select value,
                                                _ => throw new InvalidOperationException($"Resource '{resourceKey.Resource}' does not support API specifications.")
                                            }
                                            from link in value.GetStringProperty("link")
                                            select new Uri(link);
                    var downloadUri = downloadUriResult.IfErrorThrow();

                    // The link does not support authentication, so use an unauthenticated pipeline.
                    var unauthenticatedPipeline = HttpPipelineBuilder.Build(ClientOptions.Default);
                    var contentResult = await unauthenticatedPipeline.GetContent(downloadUri, cancellationToken);
                    var content = contentResult.IfErrorThrow();

                    // APIM always exports Open API v2 to JSON. Convert to YAML if needed.
                    if (specification is ApiSpecification.OpenApi openApi2
                        && openApi2.Version is OpenApiVersion.V2
                        && openApi2.Format is not OpenApiFormat.Json)
                    {
                        content = await convertOpenApiContent(content, openApi2.Format, openApi2.Version, cancellationToken);
                    }

                    return content;
            }
        }

        async ValueTask<Option<BinaryData>> getGraphQlSpecificationContents(ResourceKey resourceKey, CancellationToken cancellationToken)
        {
            var (resource, name, parents) = (resourceKey.Resource, resourceKey.Name, resourceKey.Parents);

            var schemaName = ResourceName.From("graphql").IfErrorThrow();

            IResourceWithDto schemaResource = resource switch
            {
                ApiResource => ApiSchemaResource.Instance,
                WorkspaceApiResource => WorkspaceApiSchemaResource.Instance,
                _ => throw new InvalidOperationException($"Getting schema for {resourceKey} is not supported.")
            };

            var serializerOptions = schemaResource.SerializerOptions;

            var schemaAncestors = parents.Append(resource, name);

            return from dto in await getOptionalDto(schemaResource, schemaName, schemaAncestors, cancellationToken)
                   let result = from dtoObject in JsonNodeModule.To<ApiSchemaDto>(dto, serializerOptions)
                                select dtoObject.Properties.Document?.Value
                   from contents in result.ToOption()
                   where string.IsNullOrWhiteSpace(contents) is false
                   select BinaryData.FromString(contents);
        }

        static async ValueTask<BinaryData> convertOpenApiContent(BinaryData content, OpenApiFormat targetFormat, OpenApiVersion targetVersion, CancellationToken cancellationToken)
        {
            using var stream = content.ToStream();
            var settings = GetOpenApiReaderSettings();

            var (document, diagnostic) = await OpenApiDocument.LoadAsync(stream, settings: settings, cancellationToken: cancellationToken);
            if (document is null || diagnostic is null || diagnostic.Errors.Count > 0)
            {
                throw new InvalidOperationException($"Downloaded Open API specification is invalid: {string.Join(", ", diagnostic?.Errors.Select(e => e.Message) ?? [])}");
            }

            switch (targetFormat, targetVersion)
            {
                case (OpenApiFormat.Json, OpenApiVersion.V2) when diagnostic.Format is "json" && diagnostic.SpecificationVersion is OpenApiSpecVersion.OpenApi2_0:
                    {
                        return content;
                    }
                case (OpenApiFormat.Json, OpenApiVersion.V2):
                    {
                        var newContentString = await document.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi2_0, cancellationToken);
                        return BinaryData.FromString(newContentString);
                    }
                case (OpenApiFormat.Json, OpenApiVersion.V3) when diagnostic.Format is "json" && (diagnostic.SpecificationVersion is OpenApiSpecVersion.OpenApi3_0 or OpenApiSpecVersion.OpenApi3_1):
                    {
                        return content;
                    }
                case (OpenApiFormat.Json, OpenApiVersion.V3):
                    {
                        var newContentString = await document.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_1, cancellationToken);
                        return BinaryData.FromString(newContentString);
                    }
                case (OpenApiFormat.Yaml, OpenApiVersion.V2) when diagnostic.Format is "yaml" && diagnostic.SpecificationVersion is OpenApiSpecVersion.OpenApi2_0:
                    {
                        return content;
                    }
                case (OpenApiFormat.Yaml, OpenApiVersion.V2):
                    {
                        var newContentString = await document.SerializeAsYamlAsync(OpenApiSpecVersion.OpenApi2_0, cancellationToken);
                        return BinaryData.FromString(newContentString);
                    }
                case (OpenApiFormat.Yaml, OpenApiVersion.V3) when diagnostic.Format is "yaml" && (diagnostic.SpecificationVersion is OpenApiSpecVersion.OpenApi3_0 or OpenApiSpecVersion.OpenApi3_1):
                    {
                        return content;
                    }
                case (OpenApiFormat.Yaml, OpenApiVersion.V3):
                    {
                        var newContentString = await document.SerializeAsYamlAsync(OpenApiSpecVersion.OpenApi3_1, cancellationToken);
                        return BinaryData.FromString(newContentString);
                    }
                default:
                    throw new InvalidOperationException($"Conversion to Open API specification {targetFormat} {targetVersion} is not supported.");
            }
        }
    }

    public static void ConfigureGetApiSpecificationFromFile(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureServiceDirectory(builder);

        builder.TryAddSingleton(ResolveGetApiSpecificationFromFile);
    }

    private static GetApiSpecificationFromFile ResolveGetApiSpecificationFromFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ServiceDirectory>();

        return async (resourceKey, readFile, cancellationToken) =>
            await specifications.Choose(specification => GetSpecificationFile(resourceKey, specification, serviceDirectory))
                                .Choose(async file => await getSpecification(file, readFile, cancellationToken))
                                .Head(cancellationToken);

        static async ValueTask<Option<(ApiSpecification Specification, BinaryData Contents)>> getSpecification(FileInfo file, ReadFile readFile, CancellationToken cancellationToken)
        {
            var contentsOption = await readFile(file, cancellationToken);

            return await contentsOption.BindTask(async contents => Path.GetExtension(file.FullName)
                                                                       .ToLowerInvariant() switch
            {
                ".json" or ".yaml" or ".yml" => from specification in await GetOpenApiSpecification(contents, cancellationToken)
                                                select ((ApiSpecification)specification, contents),
                ".graphql" => (ApiSpecification.GraphQl.Instance, contents),
                ".wadl" => (ApiSpecification.Wadl.Instance, contents),
                ".wsdl" => (ApiSpecification.Wsdl.Instance, contents),
                _ => Option.None,
            });
        }
    }

    private static Option<FileInfo> GetSpecificationFile(ResourceKey resourceKey, ApiSpecification specification, ServiceDirectory serviceDirectory)
    {
        var (resource, name, parents) = (resourceKey.Resource, resourceKey.Name, resourceKey.Parents);

        if (resource is not IResourceWithDirectory resourceWithDirectory)
        {
            return Option.None;
        }

        if (resource is not (ApiResource or WorkspaceApiResource))
        {
            return Option.None;
        }

        var specificationFileName = GetSpecificationFileName(specification);

        return resourceWithDirectory.GetCollectionDirectoryInfo(parents, serviceDirectory)
                                    .GetChildDirectory(name.ToString())
                                    .GetChildFile(specificationFileName);
    }

    private static string GetSpecificationFileName(ApiSpecification specification) =>
        $"specification.{specification switch
        {
            ApiSpecification.GraphQl => "graphql",
            ApiSpecification.Wsdl => "wsdl",
            ApiSpecification.Wadl => "wadl",
            ApiSpecification.OpenApi openApi when openApi.Format is OpenApiFormat.Json => "json",
            ApiSpecification.OpenApi openApi when openApi.Format is OpenApiFormat.Yaml => "yaml",
            _ => throw new InvalidOperationException($"Specification {specification} is not supported.")
        }}";

    private static async ValueTask<Option<ApiSpecification.OpenApi>> GetOpenApiSpecification(BinaryData contents, CancellationToken cancellationToken)
    {
        using var stream = contents.ToStream();
        var settings = GetOpenApiReaderSettings();

        OpenApiDiagnostic? diagnostic = null;
        try
        {
            (_, diagnostic) = await OpenApiDocument.LoadAsync(stream, settings: settings, cancellationToken: cancellationToken);
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch
#pragma warning restore CA1031 // Do not catch general exception types            
        {
        }

        if (diagnostic is null || diagnostic.Errors.Count > 0)
        {
            return Option.None;
        }

        return
            from format in diagnostic.Format switch
            {
                "json" => Option<OpenApiFormat>.Some(OpenApiFormat.Json.Instance),
                "yaml" => OpenApiFormat.Yaml.Instance,
                _ => Option.None
            }
            from version in diagnostic.SpecificationVersion switch
            {
                OpenApiSpecVersion.OpenApi2_0 => Option<OpenApiVersion>.Some(OpenApiVersion.V2.Instance),
                OpenApiSpecVersion.OpenApi3_1 => OpenApiVersion.V3.Instance,
                OpenApiSpecVersion.OpenApi3_0 => OpenApiVersion.V3.Instance,
                _ => Option.None
            }
            select new ApiSpecification.OpenApi
            {
                Format = format,
                Version = version
            };
    }

    private static OpenApiReaderSettings GetOpenApiReaderSettings()
    {
        var settings = new OpenApiReaderSettings();
        settings.AddJsonReader();
        settings.AddYamlReader();
        return settings;
    }

    public static void ConfigureWriteApiSpecificationFile(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureServiceDirectory(builder);

        builder.TryAddSingleton(ResolveWriteApiSpecificationFile);
    }

    private static WriteApiSpecificationFile ResolveWriteApiSpecificationFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ServiceDirectory>();

        return async (resourceKey, specification, contents, cancellationToken) =>
        {
            var fileOption = GetSpecificationFile(resourceKey, specification, serviceDirectory);

            await fileOption.IterTask(async file => await file.OverwriteWithBinaryData(contents, cancellationToken));
        };
    }

    public static void ConfigurePutApiSpecificationInApim(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);
        ConfigureGetResourceDtoFromApim(builder);
        ConfigurePutResourceInApim(builder);

        builder.TryAddSingleton(ResolvePutApiSpecificationInApim);
    }

    private static PutApiSpecificationInApim ResolvePutApiSpecificationInApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var getDto = provider.GetRequiredService<GetResourceDtoFromApim>();
        var putResource = provider.GetRequiredService<PutResourceInApim>();

        var resource = ApiResource.Instance;
        var ancestors = ParentChain.Empty;

        return async (resourceKey, specification, contents, cancellationToken) =>
        {
            if (resourceKey.Resource is not ApiResource && resourceKey.Resource is not WorkspaceApiResource)
            {
                throw new InvalidOperationException($"Resource '{resourceKey.Resource}' does not support API specifications.");
            }

            await (specification switch
            {
                ApiSpecification.OpenApi openApiSpecification => putOpenApiSpecification(resourceKey, openApiSpecification, contents, cancellationToken),
                ApiSpecification.Wadl wadlSpecification => putWadlSpecification(resourceKey, wadlSpecification, contents, cancellationToken),
                ApiSpecification.Wsdl wsdlSpecification => putWsdlSpecification(resourceKey, wsdlSpecification, contents, cancellationToken),
                ApiSpecification.GraphQl graphQlSpecification => putGraphQlSpecification(resourceKey, graphQlSpecification, contents, cancellationToken),
                _ => throw new InvalidOperationException($"Specification {specification} is not supported.")
            });
        };

        async ValueTask putOpenApiSpecification(ResourceKey resourceKey, ApiSpecification.OpenApi specification, BinaryData contents, CancellationToken cancellationToken)
        {
            var resource = (IResourceWithDto)resourceKey.Resource;
            var dto = await getDto(resource, resourceKey.Name, resourceKey.Parents, cancellationToken);

            dto = dto.MergeWith(new JsonObject
            {
                ["properties"] = new JsonObject
                {
                    ["format"] = (specification.Format, specification.Version) switch
                    {
                        (OpenApiFormat.Json, OpenApiVersion.V2) => "swagger-json",
                        (OpenApiFormat.Yaml, OpenApiVersion.V2) => "openapi",
                        (OpenApiFormat.Json, OpenApiVersion.V3) => "openapi+json",
                        (OpenApiFormat.Yaml, OpenApiVersion.V3) => "openapi",
                        _ => throw new InvalidOperationException($"Specification {specification} is not supported.")
                    },
                    ["value"] = contents.ToString()
                }
            }, mutateOriginal: true);

            await putSpecificationDto(resourceKey, dto, useImportQueryParameter: false, cancellationToken);
        }

        async ValueTask putSpecificationDto(ResourceKey resourceKey, JsonObject dto, bool useImportQueryParameter, CancellationToken cancellationToken)
        {
            var (resource, name, parents) = (resourceKey.Resource, resourceKey.Name, resourceKey.Parents);
            var uri = resource.GetUri(name, parents, serviceUri);

            if (useImportQueryParameter)
            {
                uri = uri.AppendQueryParam("import", true)
                         .ToUri();
            }

            var result = await pipeline.PutJson(uri, dto, cancellationToken);

            result.IfErrorThrow();
        }

        async ValueTask putWadlSpecification(ResourceKey resourceKey, ApiSpecification.Wadl specification, BinaryData contents, CancellationToken cancellationToken)
        {
            var dto = new JsonObject
            {
                ["properties"] = new JsonObject
                {
                    ["format"] = "wadl-xml",
                    ["value"] = contents.ToString()
                }
            };

            await putSpecificationDto(resourceKey, dto, useImportQueryParameter: true, cancellationToken);
        }

        async ValueTask putWsdlSpecification(ResourceKey resourceKey, ApiSpecification.Wsdl specification, BinaryData contents, CancellationToken cancellationToken)
        {
            // WSDL specification import removes the original description. Save the description.            
            var resource = (IResourceWithDto)resourceKey.Resource;
            var originalDto = await getDto(resource, resourceKey.Name, resourceKey.Parents, cancellationToken);
            var descriptionResult = from properties in originalDto.GetJsonObjectProperty("properties")
                                    from description in properties.GetStringProperty("description")
                                    select description;

            // Import the specification
            var dto = new JsonObject
            {
                ["properties"] = new JsonObject
                {
                    ["format"] = "wsdl",
                    ["value"] = contents.ToString(),
                    ["apiType"] = "soap"
                }
            };

            await putSpecificationDto(resourceKey, dto, useImportQueryParameter: true, cancellationToken);

            // Re-apply the original description
            await descriptionResult.IterTask(async description =>
            {
                if (ApiRevisionModule.IsRootName(resourceKey.Name) is false)
                {
                    return;
                }

                var newDto = await getDto(resource, resourceKey.Name, resourceKey.Parents, cancellationToken);

                newDto.GetJsonObjectProperty("properties")
                      .Iter(propertiesJson => propertiesJson["description"] = description);

                await putResource(resource, resourceKey.Name, newDto, resourceKey.Parents, cancellationToken);
            });
        }

        async ValueTask putGraphQlSpecification(ResourceKey resourceKey, ApiSpecification.GraphQl specification, BinaryData contents, CancellationToken cancellationToken)
        {
            IResourceWithDto schemaResource;
            ParentChain schemaAncestors;

            if (resourceKey.Resource is ApiResource apiResource)
            {
                schemaResource = ApiSchemaResource.Instance;
                schemaAncestors = ParentChain.From([(apiResource, resourceKey.Name)]);
            }
            else if (resourceKey.Resource is WorkspaceApiResource workspaceApiResource)
            {
                schemaResource = WorkspaceApiSchemaResource.Instance;
                schemaAncestors = ParentChain.From([(workspaceApiResource, resourceKey.Name)]);
            }
            else
            {
                throw new InvalidOperationException($"Resource '{resourceKey.Resource}' does not support GraphQL specifications.");
            }

            var schemaName = ResourceName.From("graphql").IfErrorThrow();
            var schemaDto = new JsonObject
            {
                ["properties"] = new JsonObject
                {
                    ["contentType"] = "application/vnd.ms-azure-apim.graphql.schema",
                    ["document"] = new JsonObject
                    {
                        ["value"] = contents.ToString()
                    }
                }
            };

            await putResource(schemaResource, schemaName, schemaDto, schemaAncestors, cancellationToken);
        }
    }

    private static Option<(ResourceName Name, ParentChain Ancestors)> ParseSpecificationFile(this ApiResource resource, FileInfo? file, ServiceDirectory serviceDirectory)
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
}