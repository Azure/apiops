using Azure.Core.Pipeline;
using Flurl;
using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public sealed record WorkspaceProductResource : IResourceWithInformationFile, IChildResource
{
    private WorkspaceProductResource() { }

    public string FileName { get; } = "productInformation.json";

    public string CollectionDirectoryName { get; } = "products";

    public string SingularName { get; } = "product";

    public string PluralName { get; } = "products";

    public string CollectionUriPath { get; } = "products";

    public Type DtoType { get; } = typeof(WorkspaceProductDto);

    public IResource Parent { get; } = WorkspaceResource.Instance;

    public static WorkspaceProductResource Instance { get; } = new();
}

public sealed record WorkspaceProductDto
{
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public required ProductContract Properties { get; init; }

    public sealed record ProductContract
    {
        [JsonPropertyName("displayName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? DisplayName { get; init; }

        [JsonPropertyName("description")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Description { get; init; }

        [JsonPropertyName("approvalRequired")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool? ApprovalRequired { get; init; }

        [JsonPropertyName("state")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? State { get; init; }

        [JsonPropertyName("subscriptionRequired")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool? SubscriptionRequired { get; init; }

        [JsonPropertyName("subscriptionsLimit")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int? SubscriptionsLimit { get; init; }

        [JsonPropertyName("terms")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Terms { get; init; }
    }
}

public static partial class ResourceModule
{
    private static async ValueTask PutWorkspaceProductInApim(ResourceName name,
                                                             JsonObject dto,
                                                             ParentChain parents,
                                                             HttpPipeline pipeline,
                                                             ServiceUri serviceUri,
                                                             IsResourceSupportedInApim isResourceSupported,
                                                             DoesResourceExistInApim doesResourceExist,
                                                             ListResourceNamesFromApim listNames,
                                                             DeleteResourceFromApim deleteResource,
                                                             CancellationToken cancellationToken)
    {
        var resource = WorkspaceProductResource.Instance;

        var resourceKey = ResourceKey.From(resource, name, parents);

        var alreadyExists = await doesResourceExist(resourceKey, cancellationToken);

        var uri = resource.GetUri(name, parents, serviceUri);
        var result = await pipeline.PutJson(uri, dto, cancellationToken);
        result.IfErrorThrow();

        if (alreadyExists)
        {
            return;
        }

        await deleteSubscriptions(name, parents, cancellationToken);
        await deleteProductGroups(name, parents, cancellationToken);

        async ValueTask deleteSubscriptions(ResourceName productName, ParentChain productParents, CancellationToken cancellationToken)
        {
            var subscriptionResource = WorkspaceSubscriptionResource.Instance;
            if (await isResourceSupported(subscriptionResource, cancellationToken) is false)
            {
                return;
            }

            var serializerOptions = ((IResourceWithDto)subscriptionResource).SerializerOptions;
            var subscriptionAncestors = productParents;
            var productPropertyName = subscriptionResource.OptionalReferencedResourceDtoProperties
                                                          .Find(resource)
                                                          .IfNone(() => throw new InvalidOperationException("Workspace subscription resource does not reference workspace product resource."));

            var subscriptionsUri = subscriptionResource.GetCollectionUri(subscriptionAncestors, serviceUri)
                                                       .AppendQueryParam("$filter", $"endswith({productPropertyName}, '{productName}')", isEncoded: true)
                                                       .ToUri();

            await pipeline.ListJsonObjects(subscriptionsUri, cancellationToken)
                          .Choose(json =>
                          {
                              var result = from nameString in json.GetStringProperty("name")
                                           from subscriptionName in ResourceName.From(nameString)
                                           from subscriptionDto in JsonNodeModule.To<WorkspaceSubscriptionDto>(json, serializerOptions)
                                           select (subscriptionName, subscriptionDto);

                              return from x in result.ToOption()
                                     let scope = x.subscriptionDto.Properties.Scope ?? string.Empty
                                     where scope.Split('/').Last().Equals(productName.ToString(), StringComparison.OrdinalIgnoreCase)
                                     select ResourceKey.From(subscriptionResource, x.subscriptionName, subscriptionAncestors);
                          })
                          .IterTaskParallel(async subscriptionResourceKey => await deleteResource(subscriptionResourceKey,
                                                                                                  ignoreNotFound: true,
                                                                                                  waitForCompletion: true,
                                                                                                  cancellationToken),
                                            maxDegreeOfParallelism: Option.None,
                                            cancellationToken);
        }

        async ValueTask deleteProductGroups(ResourceName productName, ParentChain productParents, CancellationToken cancellationToken)
        {
            var productGroupResource = WorkspaceProductGroupResource.Instance;
            if (await isResourceSupported(productGroupResource, cancellationToken) is false)
            {
                return;
            }

            var productGroupAncestors = productParents.Append(resource, productName);

            await listNames(productGroupResource, productGroupAncestors, cancellationToken)
                    .Select(productGroupName => ResourceKey.From(productGroupResource, productGroupName, productGroupAncestors))
                    .IterTaskParallel(async productGroupResourceKey => await deleteResource(productGroupResourceKey,
                                                                                            ignoreNotFound: true,
                                                                                            waitForCompletion: true,
                                                                                            cancellationToken),
                                      maxDegreeOfParallelism: Option.None,
                                      cancellationToken);
        }
    }
}
