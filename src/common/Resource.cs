using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace common;

public sealed record ResourceName : IEquatable<ResourceName>
{
    private readonly string value;

    private ResourceName(string value)
    {
        this.value = value;
    }

    public override string ToString() => value;

    public bool Equals(ResourceName? other) =>
        other is not null && value.Equals(other.value, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(value);

    public static Result<ResourceName> From(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? Error.From("Resource name cannot be null or whitespace.")
            : new ResourceName(value);
}

/// <summary>
/// Base interface for all API management resources.
/// </summary>
public interface IResource
{
    string SingularName { get; }
    string PluralName { get; }

    public string ConfigurationKey =>
        // By default, use the plural name in camelCase as the configuration key.
        new([.. PluralName.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                          .SelectMany<string, char>((word, index) => word switch
                          {
                              // Lowercase the first character of the first word
                              [var first, .. var rest] when index == 0 => [char.ToLowerInvariant(first), .. rest],
                              // Uppercase the first character of subsequent words
                              [var first, .. var rest] => [char.ToUpperInvariant(first), .. rest],
                              _ => []
                          })]);

#pragma warning disable CA1056 // URI-like properties should not be strings. This is not a URI, but a path to a collection of resources.
    public string CollectionUriPath { get; }
#pragma warning restore CA1056 // URI-like properties should not be strings
}

public interface IResourceWithDto : IResource
{
    JsonSerializerOptions SerializerOptions => JsonSerializerOptions.Web;
    Type DtoType { get; }
}

public interface IResourceWithDirectory : IResource
{
    string CollectionDirectoryName { get; }
}

public interface IResourceWithInformationFile : IResourceWithDirectory, IResourceWithDto
{
    string FileName { get; }
}

public interface IChildResource : IResource
{
    IResource Parent { get; }
}

public interface ICompositeResource : IResourceWithInformationFile
{
    IResourceWithDirectory Primary { get; }
    IResourceWithDirectory Secondary { get; }

    public new string SingularName => Secondary.SingularName;
    string IResource.SingularName => SingularName;

    public new string PluralName => Secondary.PluralName;
    string IResource.PluralName => PluralName;

    public new string CollectionDirectoryName => Secondary.CollectionDirectoryName;
    string IResourceWithDirectory.CollectionDirectoryName => CollectionDirectoryName;

#pragma warning disable CA1056 // URI-like properties should not be strings
    public new string CollectionUriPath => Secondary.CollectionUriPath;
#pragma warning restore CA1056 // URI-like properties should not be strings
    string IResource.CollectionUriPath => CollectionUriPath;

    public new Type DtoType => typeof(DirectCompositeDto);
    Type IResourceWithDto.DtoType => DtoType;
}

public sealed record DirectCompositeDto
{
    public static DirectCompositeDto Instance { get; } = new();
}

/// <summary>
/// These are composite resources that connected via a link entity (…/primary/primaryName/secondaryLinks/secondaryLinkName)
/// </summary>
public interface ILinkResource : ICompositeResource
{
#pragma warning disable CA1056 // URI-like properties should not be strings
    public new string CollectionUriPath => $"{Secondary.SingularName}Links";
#pragma warning restore CA1056 // URI-like properties should not be strings
    string IResource.CollectionUriPath => CollectionUriPath;

    string DtoPropertyNameForLinkedResource { get; }

    public new Type DtoType => typeof(LinkResourceDto);
    Type ICompositeResource.DtoType => DtoType;
}

public sealed record LinkResourceDto
{
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public required string Name { get; init; }
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public required JsonObject Properties { get; init; }
}

public interface IResourceWithReference : IResourceWithInformationFile
{
    ImmutableDictionary<IResource, string> MandatoryReferencedResourceDtoProperties => [];

    ImmutableDictionary<IResource, string> OptionalReferencedResourceDtoProperties => [];
}

public interface IPolicyResource : IResourceWithDto
{
    public new string SingularName => "policy";
    string IResource.SingularName => SingularName;

    public new string PluralName => "policies";
    string IResource.PluralName => PluralName;

#pragma warning disable CA1056 // URI-like properties should not be strings
    public new string CollectionUriPath => "policies";
#pragma warning restore CA1056 // URI-like properties should not be strings
    string IResource.CollectionUriPath => CollectionUriPath;

    public new Type DtoType => typeof(PolicyDto);
    Type IResourceWithDto.DtoType => DtoType;

    // We don't want to encode the JSON for policies, as it may contain XML that needs to be preserved exactly.
    public new JsonSerializerOptions SerializerOptions => new(JsonSerializerOptions.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    JsonSerializerOptions IResourceWithDto.SerializerOptions => SerializerOptions;
}

public sealed record PolicyDto
{
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public required PolicyContract Properties { get; init; }

    public sealed record PolicyContract
    {
        [JsonPropertyName("description")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Description { get; init; }

        [JsonPropertyName("format")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Format { get; init; }

        [JsonPropertyName("value")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Value { get; init; }
    }
}

public sealed record ParentChain : IReadOnlyCollection<(IResource Resource, ResourceName Name)>
{
    private readonly ImmutableArray<(IResource Resource, ResourceName Name)> parents;

    private ParentChain(IEnumerable<(IResource Resource, ResourceName Name)> parents)
    {
        this.parents = [.. parents];
    }

    public ParentChain Prepend(IResource resource, ResourceName name) =>
        ParentChain.From([(resource, name), .. parents]);

    public ParentChain Append(IResource resource, ResourceName name) =>
        ParentChain.From([.. parents, (resource, name)]);

    public bool Equals(ParentChain? other) =>
        other is not null &&
        (ReferenceEquals(this, other) || parents.SequenceEqual(other.parents));

    public override int GetHashCode() =>
        parents.Aggregate(0, (hash, parent) => HashCode.Combine(hash, parent.Resource, parent.Name));

    public static ParentChain Empty { get; } = new([]);

    public static ParentChain From(IEnumerable<(IResource Resource, ResourceName Name)> parents) =>
        new(parents);

    // IReadOnlyCollection<T> implementations
    public int Count => parents.Length;

    public IEnumerator<(IResource Resource, ResourceName Name)> GetEnumerator() =>
        ((IEnumerable<(IResource Resource, ResourceName Name)>)parents).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed record ResourceKey
{
    public required IResource Resource { get; init; }
    public required ResourceName Name { get; init; }
    public required ParentChain Parents { get; init; }

    public override string ToString() =>
        Parents.Append(Resource, Name)
               .ToResourceId()
               .Trim('/');

    public static ResourceKey From(IResource resource, ResourceName name) =>
        From(resource, name, ParentChain.Empty);

    public static ResourceKey From(IResource resource, ResourceName name, ParentChain parents) =>
        new()
        {
            Resource = resource,
            Name = name,
            Parents = parents
        };

    public ParentChain AsParentChain() =>
        Parents.Append(Resource, Name);
}

public static partial class ResourceModule
{
    public static string ToResourceId(this ParentChain parents)
    {
        var segments = from parent in parents
                       from path in new[]
                       {
                           parent.Resource.CollectionUriPath,
                           parent.Name.ToString()
                       }
                       select path;

        return $"/{string.Join('/', segments)}";
    }
}

public static class ResourceExtensions
{
    public static Option<IResource> GetTraversalPredecessor(this IResource resource) =>
        resource switch
        {
            IChildResource child => Option.Some(child.Parent),
            ICompositeResource composite => Option.Some<IResource>(composite.Primary),
            _ => Option.None
        };

    public static ImmutableArray<IResource> GetTraversalPredecessorHierarchy(this IResource resource) =>
        resource.GetTraversalPredecessor()
                .Map(predecessor => ImmutableArray.Create([.. predecessor.GetTraversalPredecessorHierarchy(), predecessor]))
                .IfNone(() => []);

    public static ImmutableHashSet<IResource> ListDependencies(this IResource resource)
    {
        var list = new List<IResource>();

        if (resource is IChildResource child)
        {
            list.Add(child.Parent);
        }

        if (resource is ICompositeResource composite)
        {
            list.Add(composite.Primary);
            list.Add(composite.Secondary);
        }

        if (resource is IResourceWithReference withReference)
        {
            list.AddRange(withReference.MandatoryReferencedResourceDtoProperties.Keys);
            list.AddRange(withReference.OptionalReferencedResourceDtoProperties.Keys);
        }

        if (resource is IPolicyResource)
        {
            // Policies can reference named values in their content
            list.Add(NamedValueResource.Instance);

            // Policies can reference policy fragments
            if (resource is not PolicyFragmentResource)
            {
                list.Add(PolicyFragmentResource.Instance);
            }
        }

        return [.. list];
    }
}

public static partial class ResourceModule
{
    /// <summary>
    /// Transforms an absolute resource ID to a relative ID that is not tied to a specific service.
    /// For example, "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg1/providers/Microsoft.ApiManagement/service/apimService1/loggers/azuremonitor"
    /// becomes "/loggers/azuremonitor".
    /// </summary>
    internal static string SetAbsoluteToRelativeId(string absoluteResourceId)
    {
        if (string.IsNullOrWhiteSpace(absoluteResourceId))
        {
            return string.Empty;
        }

        const string delimiter = "Microsoft.ApiManagement/service/";
        var delimiterIndex = absoluteResourceId.IndexOf(delimiter, StringComparison.OrdinalIgnoreCase);

        if (delimiterIndex == -1)
        {
            return absoluteResourceId;
        }

        var startIndex = delimiterIndex + delimiter.Length;
        var remainingPath = absoluteResourceId[startIndex..];
        var pathSegments = remainingPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        return pathSegments.Length < 2
                ? absoluteResourceId
                : $"/{string.Join('/', pathSegments.Skip(1))}";
    }

    /// <summary>
    /// Link resources store the secondary resource name in the DTO.
    /// </summary>
    public static Option<ResourceName> GetSecondaryResourceName(this ILinkResource linkResource, JsonObject dto) =>
        dto.GetJsonObjectProperty("properties")
           .Bind(properties => properties.GetStringProperty(linkResource.DtoPropertyNameForLinkedResource))
           .Map(name => name.Split('/').LastOrDefault())
           .Bind(ResourceName.From)
           .ToOption();
}