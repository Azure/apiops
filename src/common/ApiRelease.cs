using Azure.Core.Pipeline;
using System;
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public sealed record ApiReleaseResource : IResourceWithReference, IChildResource
{
    private ApiReleaseResource() { }

    public string FileName { get; } = "apiReleaseInformation.json";

    public string CollectionDirectoryName { get; } = "releases";

    public string SingularName { get; } = "release";

    public string PluralName { get; } = "releases";

    public string CollectionUriPath { get; } = "releases";

    public Type DtoType { get; } = typeof(ApiReleaseDto);

    public IResource Parent { get; } = ApiResource.Instance;

    public ImmutableDictionary<IResource, string> OptionalReferencedResourceDtoProperties { get; } =
        ImmutableDictionary.Create<IResource, string>()
                           .Add(ApiResource.Instance, nameof(ApiReleaseDto.Properties.ApiId));

    public static ApiReleaseResource Instance { get; } = new();
}

public sealed record ApiReleaseDto
{
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public required ApiReleaseContract Properties { get; init; }

    public record ApiReleaseContract
    {
        [JsonPropertyName("apiId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? ApiId { get; init; }

        [JsonPropertyName("notes")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Notes { get; init; }
    }
}

public static partial class ResourceModule
{
    private static async ValueTask PutApiReleaseInApim(ResourceName name,
                                                       JsonObject dto,
                                                       ParentChain ancestors,
                                                       HttpPipeline pipeline,
                                                       ServiceUri serviceUri,
                                                       CancellationToken cancellationToken)
    {
        var resource = ApiReleaseResource.Instance;
        var resourceKey = ResourceKey.From(resource, name, ancestors);

        var uri = resource.GetUri(name, ancestors, serviceUri);
        var formattedDto = formatDto(dto);
        
        var result = await pipeline.PutJson(uri, formattedDto, cancellationToken);
        result.IfErrorThrow();

        // Set the API ID if it's missing from the DTO
        JsonObject formatDto(JsonObject dtoJson)
        {
            var serializerOptions = ((IResourceWithDto)resource).SerializerOptions;

            var result = from dto in JsonNodeModule.To<ApiReleaseDto>(dtoJson, serializerOptions)
                         let formattedDto = dto with
                         {
                             Properties = dto.Properties with
                             {
                                 ApiId = dto.Properties.ApiId ?? ancestors.ToResourceId()
                             }
                         }
                         from formattedJson in JsonObjectModule.From(formattedDto, serializerOptions)
                         select formattedJson;

            return result.IfError(_ => dtoJson);
        }
    }
}