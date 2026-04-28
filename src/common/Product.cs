using Azure.Core.Pipeline;
using Flurl;
using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public sealed record ProductResource : IResourceWithInformationFile
{
    private ProductResource() { }

    public string FileName { get; } = "productInformation.json";

    public string CollectionDirectoryName { get; } = "products";

    public string SingularName { get; } = "product";

    public string PluralName { get; } = "products";

    public string CollectionUriPath { get; } = "products";

    public Type DtoType { get; } = typeof(ProductDto);

    public static ProductResource Instance { get; } = new();
}

public sealed record ProductDto
{
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public required ProductContract Properties { get; init; }

    public record ProductContract
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
    private static async ValueTask PutProductInApim(ResourceName name,
                                                    JsonObject dto,
                                                    HttpPipeline pipeline,
                                                    ServiceUri serviceUri,
                                                    IsResourceSupportedInApim isResourceSupported,
                                                    DoesResourceExistInApim doesResourceExist,
                                                    ListResourceNamesFromApim listNames,
                                                    DeleteResourceFromApim deleteResource,
                                                    CancellationToken cancellationToken)
    {
        var resource = ProductResource.Instance;
        var ancestors = ParentChain.Empty;
        var resourceKey = ResourceKey.From(resource, name, ancestors);

        var alreadyExists = await doesResourceExist(resourceKey, cancellationToken);

        var uri = resource.GetUri(name, ancestors, serviceUri);
        var result = await pipeline.PutJson(uri, dto, cancellationToken);
        result.IfErrorThrow();

        // Delete automatically created resources
        if (alreadyExists is false)
        {
            await deleteSubscriptions(name, cancellationToken);
            await deleteProductGroups(name, cancellationToken);
        }

        async ValueTask deleteSubscriptions(ResourceName productName, CancellationToken cancellationToken)
        {
            var subscriptionResource = SubscriptionResource.Instance;
            if (await isResourceSupported(subscriptionResource, cancellationToken) is false)
            {
                return;
            }

            var serializerOptions = ((IResourceWithDto)subscriptionResource).SerializerOptions;
            var subscriptionAncestors = ancestors;
            var productPropertyName = subscriptionResource.OptionalReferencedResourceDtoProperties
                                                          .Find(resource)
                                                          .IfNone(() => throw new InvalidOperationException("Subscription resource does not reference product resource."));

            var uri = subscriptionResource.GetCollectionUri(subscriptionAncestors, serviceUri)
                                          .AppendQueryParam("$filter", $"endswith({productPropertyName}, '{productName}')", isEncoded: true) // No quotes around property name, otherwise fails
                                          .ToUri();

            await pipeline.ListJsonObjects(uri, cancellationToken)
                          .Choose(json =>
                          {
                              var result = from nameString in json.GetStringProperty("name")
                                           from name in ResourceName.From(nameString)
                                           from dto in JsonNodeModule.To<SubscriptionDto>(json, serializerOptions)
                                           select (name, dto);

                              return from x in result.ToOption()
                                     let scope = x.dto.Properties.Scope ?? string.Empty
                                     where scope.Split('/').Last().Equals(productName.ToString(), StringComparison.OrdinalIgnoreCase)
                                     select ResourceKey.From(subscriptionResource, x.name, subscriptionAncestors);
                          })
                          .IterTaskParallel(async subscriptionResourceKey => await deleteResource(subscriptionResourceKey, ignoreNotFound: true, waitForCompletion: true, cancellationToken),
                                            maxDegreeOfParallelism: Option.None,
                                            cancellationToken);
        }

        async ValueTask deleteProductGroups(ResourceName productName, CancellationToken cancellationToken)
        {
            var productGroupResource = ProductGroupResource.Instance;
            if (await isResourceSupported(productGroupResource, cancellationToken) is false)
            {
                return;
            }

            var productGroupAncestors = ancestors.Append(resource, productName);

            await listNames(productGroupResource, productGroupAncestors, cancellationToken)
                    .Select(productGroupName => ResourceKey.From(productGroupResource, productGroupName, productGroupAncestors))
                    .IterTaskParallel(async productGroupResourceKey => await deleteResource(productGroupResourceKey, ignoreNotFound: true, waitForCompletion: true, cancellationToken),
                                      maxDegreeOfParallelism: Option.None,
                                      cancellationToken);
        }
    }
}