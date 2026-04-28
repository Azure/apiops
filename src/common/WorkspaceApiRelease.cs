using Azure.Core.Pipeline;
using System;
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public sealed record WorkspaceApiReleaseResource : IResourceWithReference, IChildResource
{
    private WorkspaceApiReleaseResource() { }

    public string FileName { get; } = "apiReleaseInformation.json";

    public string CollectionDirectoryName { get; } = "releases";

    public string SingularName { get; } = "release";

    public string PluralName { get; } = "releases";

    public string CollectionUriPath { get; } = "releases";

    public Type DtoType { get; } = typeof(WorkspaceApiReleaseDto);

    public IResource Parent { get; } = WorkspaceApiResource.Instance;

    public ImmutableDictionary<IResource, string> OptionalReferencedResourceDtoProperties { get; } =
        ImmutableDictionary.Create<IResource, string>()
                           .Add(WorkspaceApiResource.Instance, nameof(WorkspaceApiReleaseDto.Properties.ApiId));

    public static WorkspaceApiReleaseResource Instance { get; } = new();
}

public sealed record WorkspaceApiReleaseDto
{
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public required WorkspaceApiReleaseContract Properties { get; init; }

    public sealed record WorkspaceApiReleaseContract
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
    private static async ValueTask PutWorkspaceApiReleaseInApim(ResourceName name,
                                                                JsonObject dto,
                                                                ParentChain parents,
                                                                HttpPipeline pipeline,
                                                                ServiceUri serviceUri,
                                                                CancellationToken cancellationToken)
    {
        var resource = WorkspaceApiReleaseResource.Instance;
        var uri = resource.GetUri(name, parents, serviceUri);
        var formattedDto = formatDto(dto);

        var result = await pipeline.PutJson(uri, formattedDto, cancellationToken);
        result.IfErrorThrow();

        JsonObject formatDto(JsonObject dtoJson)
        {
            var serializerOptions = ((IResourceWithDto)resource).SerializerOptions;

            var result = from dtoObject in JsonNodeModule.To<WorkspaceApiReleaseDto>(dtoJson, serializerOptions)
                         let formattedDtoObject = dtoObject with
                         {
                             Properties = dtoObject.Properties with
                             {
                                 ApiId = dtoObject.Properties.ApiId ?? parents.ToResourceId()
                             }
                         }
                         from formattedJson in JsonObjectModule.From(formattedDtoObject, serializerOptions)
                         select formattedJson;

            return result.IfError(_ => dtoJson);
        }
    }
}
