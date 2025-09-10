using Azure.Core.Pipeline;
using DotNext.Threading;
using Flurl;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

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

public sealed record ResourceAncestors : IReadOnlyCollection<(IResource Resource, ResourceName Name)>
{
    private readonly ImmutableArray<(IResource Resource, ResourceName Name)> ancestors;

    private ResourceAncestors(IEnumerable<(IResource Resource, ResourceName Name)> ancestors)
    {
        this.ancestors = [.. ancestors];
    }

    public ResourceAncestors Prepend(IResource resource, ResourceName name) =>
        ResourceAncestors.From([(resource, name), .. ancestors]);

    public ResourceAncestors Append(IResource resource, ResourceName name) =>
        ResourceAncestors.From([.. ancestors, (resource, name)]);

    public bool Equals(ResourceAncestors? other) =>
        other is not null &&
        (ReferenceEquals(this, other) || ancestors.SequenceEqual(other.ancestors));

    public override int GetHashCode() =>
        ancestors.Aggregate(0, (hash, ancestor) => HashCode.Combine(hash, ancestor.Resource, ancestor.Name));

    public static ResourceAncestors Empty { get; } = new([]);

    public static ResourceAncestors From(IEnumerable<(IResource Resource, ResourceName Name)> ancestors) =>
        new(ancestors);

    // IReadOnlyCollection<T> implementations
    public int Count => ancestors.Length;

    public IEnumerator<(IResource Resource, ResourceName Name)> GetEnumerator() => ((IEnumerable<(IResource Resource, ResourceName Name)>)ancestors).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public static class ResourceModule
{
    public static Activity? TagAncestors(this Activity? activity, ResourceAncestors ancestors) =>
        activity is null
            ? null
            : ancestors.Aggregate(activity,
                                  (accumulate, ancestor) => accumulate.SetTag(ancestor.Resource.SingularName, ancestor.Name.ToString()));

    public static string ToLogString(this ResourceAncestors ancestors)
    {
        var messages = ancestors.Select(ancestor => $" in {ancestor.Resource.SingularName} '{ancestor.Name}'");

        return string.Concat(messages);
    }

    public static string ToResourceId(this ResourceAncestors ancestors)
    {
        var segments = from ancestor in ancestors
                       from path in new[]
                       {
                           ancestor.Resource.CollectionUriPath,
                           ancestor.Name.ToString()
                       }
                       select path;

        return $"/{string.Join('/', segments)}";
    }
}

public static class ResourceTraversalExtensions
{
    public static Option<IResource> GetTraversalPredecessor(this IResource resource) =>
        resource switch
        {
            IChildResource child => Option.Some(child.Parent),
            ICompositeResource composite => Option.Some<IResource>(composite.Primary),
            _ => Option.None
        };

    public static ImmutableHashSet<IResource> GetSortingPredecessors(this IResource resource)
    {
        List<IResource> predecessors = [];

        if (resource is IChildResource child)
        {
            predecessors.Add(child.Parent);
        }

        if (resource is ICompositeResource composite)
        {
            predecessors.Add(composite.Primary);
            predecessors.Add(composite.Secondary);
        }

        if (resource is IResourceWithReference withReference)
        {
            predecessors.AddRange(withReference.MandatoryReferencedResourceDtoProperties.Keys);
            predecessors.AddRange(withReference.OptionalReferencedResourceDtoProperties.Keys);
        }

        return [.. predecessors];
    }
}

public static class ResourceFileSystemExtensions
{
    public static async ValueTask WriteInformationFile(this IResourceWithInformationFile resource, ResourceName name, JsonObject dto, ResourceAncestors ancestors, ServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        var file = resource switch
        {
            ILinkResource linkResource => linkResource.GetInformationFile(dto, ancestors, serviceDirectory)
                                                      .IfErrorThrow(),
            _ => resource.GetInformationFile(name, ancestors, serviceDirectory)
        };

        var formattedDto = resource.FormatInformationFileDto(dto);

        await file.OverwriteWithJson(formattedDto, cancellationToken);
    }

    private static FileInfo GetInformationFile(this IResourceWithInformationFile resource, ResourceName name, ResourceAncestors ancestors, ServiceDirectory serviceDirectory) =>
        resource switch
        {
            ILinkResource linkResource => throw new InvalidOperationException($"We don't have enough information to get the file for link resources. Use the {nameof(ILinkResource)} extension that takes a {nameof(JsonObject)} DTO parameter."),
            _ => resource.GetCollectionDirectoryInfo(ancestors, serviceDirectory)
                         .GetChildDirectory(name.ToString())
                         .GetChildFile(resource.FileName)
        };

    /// <remarks>
    /// The directory name is derived from the linked resource ID in the DTO.
    /// </remarks>
    private static Result<FileInfo> GetInformationFile(this ILinkResource resource, JsonObject dto, ResourceAncestors ancestors, ServiceDirectory serviceDirectory) =>
        from properties in dto.GetJsonObjectProperty("properties")
        from id in properties.GetStringProperty(resource.DtoPropertyNameForLinkedResource)
                             .MapError(error => $"Could not find '{resource.DtoPropertyNameForLinkedResource}' in DTO for {resource.SingularName}{ancestors.ToLogString()}. {error}")
        let secondaryResourceName = id.Split('/').Last()
        select resource.GetCollectionDirectoryInfo(ancestors, serviceDirectory)
                       .GetChildDirectory(secondaryResourceName)
                       .GetChildFile(resource.FileName);

    private static DirectoryInfo GetCollectionDirectoryInfo(this IResourceWithDirectory resource, ResourceAncestors ancestors, ServiceDirectory serviceDirectory) =>
        ancestors.Aggregate(serviceDirectory.ToDirectoryInfo(),
                            (directory, ancestor) => ancestor.Resource switch
                            {
                                IResourceWithDirectory ancestorResource =>
                                    directory.GetChildDirectory(ancestorResource.CollectionDirectoryName)
                                             .GetChildDirectory(ancestor.Name.ToString()),
                                _ => throw new InvalidOperationException($"Ancestor resource '{ancestor.Name}' of type {ancestor.Resource.GetType()} is not an {nameof(IResourceWithDirectory)}.")
                            })
                .GetChildDirectory(resource.CollectionDirectoryName);

    public static JsonObject FormatInformationFileDto(this IResourceWithInformationFile resource, JsonObject dto)
    {
        var formattedDto = dto;

        if (resource is PolicyFragmentResource policyFragmentResource)
        {
            formattedDto = policyFragmentResource.FormatInformationFileDto(formattedDto);
        }

        if (resource is ILinkResource linkResource)
        {
            formattedDto = linkResource.FormatInformationFileDto(ResourceName.From(dto["name"]?.ToString() ?? string.Empty).IfErrorThrow(), formattedDto);
        }

        if (resource is IResourceWithReference withReference)
        {
            formattedDto = withReference.FormatInformationFileDto(formattedDto);
        }

        return formattedDto;
    }

    /// <summary>
    /// Remove `properties.format` and `properties.value` from the information file DTO. Policy contents will be stored separately.
    /// </summary>
    private static JsonObject FormatInformationFileDto(this PolicyFragmentResource resource, JsonObject dto) =>
        dto.GetJsonObjectProperty("properties")
           .Map(properties => dto.SetProperty("properties", properties.RemoveProperty("format")
                                                                      .RemoveProperty("value")))
           .IfError(_ => dto);

    /// <summary>
    /// Transform the absolute resource ID in the link property to a relative ID and ensure the DTO contains the resource name.
    /// </summary>
    private static JsonObject FormatInformationFileDto(this ILinkResource resource, ResourceName name, JsonObject dto)
    {
        // Transform the absolute resource ID in the link property to a relative ID
        var updatedDto = SetAbsoluteToRelativeId(dto, resource.DtoPropertyNameForLinkedResource);

        // Ensure the DTO contains the resource name
        return updatedDto.SetProperty("name", name.ToString());
    }

    /// <summary>
    /// Transform all reference absolute resource IDs in the DTO to relative IDs.
    /// </summary>
    private static JsonObject FormatInformationFileDto(this IResourceWithReference resource, JsonObject dto) =>
        resource.MandatoryReferencedResourceDtoProperties
                .Union(resource.OptionalReferencedResourceDtoProperties)
                .DistinctBy(kvp => kvp.Value)
                .Aggregate(dto, (currentDto, kvp) => SetAbsoluteToRelativeId(currentDto, kvp.Value));

    private static JsonObject SetAbsoluteToRelativeId(JsonObject dto, string idPropertyName)
    {
        var updatedDto = from properties in dto.GetJsonObjectProperty("properties")
                         from id in properties.GetStringProperty(idPropertyName)
                         let formattedId = toRelativeId(id)
                         let updatedProperties = properties.SetProperty(idPropertyName, formattedId)
                         select dto.SetProperty("properties", updatedProperties);

        return updatedDto.IfError(_ => dto);

        /// <summary>
        /// Transforms an absolute resource ID to a relative ID that is not tied to a specific service.
        /// For example, "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg1/providers/Microsoft.ApiManagement/service/apimService1/loggers/azuremonitor"
        /// becomes "/loggers/azuremonitor".
        /// </summary>
        static string toRelativeId(string absoluteResourceId)
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
    }

    public static async ValueTask<Option<JsonObject>> GetInformationFileDto(this IResourceWithInformationFile resource, ResourceName name, ResourceAncestors ancestors, ServiceDirectory serviceDirectory, ReadFile readFile, CancellationToken cancellationToken)
    {
        if (resource is ILinkResource)
        {
            throw new InvalidOperationException($"We don't have enough information to get the DTO for link resources. Use the {nameof(ILinkResource)} extension that takes a {nameof(GetSubDirectories)} parameter.");
        }

        var file = resource.GetInformationFile(name, ancestors, serviceDirectory);

        return from contents in await readFile(file, cancellationToken)
               from jsonObject in JsonObjectModule.From(contents, resource.SerializerOptions).ToOption()
               select jsonObject;
    }

    public static async ValueTask<Option<JsonObject>> GetInformationFileDto(this ILinkResource resource, ResourceName name, ResourceAncestors ancestors, ServiceDirectory serviceDirectory, GetSubDirectories getSubDirectories, ReadFile readFile, CancellationToken cancellationToken)
    {
        var collectionDirectory = resource.GetCollectionDirectoryInfo(ancestors, serviceDirectory);

        return await getSubDirectories(collectionDirectory).BindTask(parseDirectories);

        async ValueTask<Option<JsonObject>> parseDirectories(IEnumerable<DirectoryInfo> directories) =>
            await directories.Choose(parseDirectory)
                             .Head(cancellationToken);

        async ValueTask<Option<JsonObject>> parseDirectory(DirectoryInfo directory)
        {
            var file = directory.GetChildFile(resource.FileName);

            return from contents in await readFile(file, cancellationToken)
                   let nameJsonResult = from jsonObject in JsonObjectModule.From(contents, resource.SerializerOptions)
                                        from name in jsonObject.GetStringProperty("name")
                                        select (name, jsonObject)
                   from nameJson in nameJsonResult.ToOption()
                   where nameJson.name == name.ToString()
                   select nameJson.jsonObject;
        }
    }

    public static Option<(ResourceName Name, ResourceAncestors Ancestors)> ParseInformationFile(this IResourceWithInformationFile resource, FileInfo? file, ServiceDirectory serviceDirectory)
    {
        if (file is null)
        {
            return Option.None;
        }

        if (file.Name != resource.FileName)
        {
            return Option.None;
        }

        if (resource is ILinkResource)
        {
            throw new InvalidOperationException($"We don't have enough information to parse link resources. Use the {nameof(ILinkResource)} extension that takes a {nameof(ReadFile)} parameter.");
        }

        return resource.Parse(file.Directory, serviceDirectory);
    }

    private static Option<(ResourceName Name, ResourceAncestors Ancestors)> Parse(this IResourceWithDirectory resource, DirectoryInfo? directory, ServiceDirectory serviceDirectory)
    {
        if (directory is null)
        {
            return Option.None;
        }

        if ((directory.Parent?.Name == resource.CollectionDirectoryName) is false)
        {
            return Option.None;
        }

        if (resource is ILinkResource)
        {
            throw new InvalidOperationException($"We don't have enough information to parse link resources. Use the {nameof(ILinkResource)} extension that takes a {nameof(ReadFile)} parameter.");
        }

        return from ancestors in resource.ParseAncestors(directory, serviceDirectory)
               from name in ResourceName.From(directory.Name).ToOption()
               select (name, ancestors);
    }

    private static Option<ResourceAncestors> ParseAncestors(this IResourceWithDirectory resource, DirectoryInfo? directory, ServiceDirectory serviceDirectory) =>
        resource.GetTraversalPredecessor()
                .Match(// Predecessor must be an IResourceWithDirectory. If it is, parse it and get its ancestors.
                       predecessor => predecessor is IResourceWithDirectory predecessorResourceWithDirectory
                                        ? from parent in predecessorResourceWithDirectory.Parse(directory?.Parent?.Parent, serviceDirectory)
                                          select parent.Ancestors.Append(predecessor, parent.Name)
                                        : Option.None,
                       // Resource has no predecessor. Make sure the path is correct relative to the service directory.
                       () => serviceDirectory.ToDirectoryInfo().FullName == directory?.Parent?.Parent?.FullName
                                ? Option.Some(ResourceAncestors.Empty)
                                : Option.None);

    public static async ValueTask<Option<(ResourceName Name, ResourceAncestors Ancestors)>> ParseInformationFile(this ILinkResource resource, FileInfo? file, ServiceDirectory serviceDirectory, ReadFile readFile, CancellationToken cancellationToken)
    {
        if (file is null)
        {
            return Option.None;
        }

        if (file.Name != resource.FileName)
        {
            return Option.None;
        }

        return from contents in await readFile(file, cancellationToken)
               from ancestors in resource.ParseAncestors(file.Directory, serviceDirectory)
               from dto in JsonObjectModule.From(contents, resource.SerializerOptions).ToOption()
               from expectedFile in resource.GetInformationFile(dto, ancestors, serviceDirectory).ToOption()
               where expectedFile.FullName == file.FullName
               from name in resource.ParseName(contents)
               select (name, ancestors);
    }

    private static Option<ResourceName> ParseName(this ILinkResource resource, BinaryData contents)
    {
        var result = from jsonObject in JsonObjectModule.From(contents, resource.SerializerOptions)
                     from nameString in jsonObject.GetStringProperty("name")
                     from name in ResourceName.From(nameString)
                     select name;

        return result.ToOption();
    }

    public static async ValueTask WritePolicyFile(this IPolicyResource resource, ResourceName name, JsonObject dto, ResourceAncestors ancestors, ServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        var contentsResult = from properties in dto.GetJsonObjectProperty("properties")
                             from value in properties.GetStringProperty("value")
                             select BinaryData.FromString(value);

        var contents = contentsResult.IfErrorThrow();

        var file = resource.GetPolicyFile(name, ancestors, serviceDirectory);

        await file.OverwriteWithBinaryData(contents, cancellationToken);
    }

    private static FileInfo GetPolicyFile(this IPolicyResource resource, ResourceName name, ResourceAncestors ancestors, ServiceDirectory serviceDirectory)
    {
        switch (resource)
        {
            case PolicyFragmentResource policyFragmentResource:
                return policyFragmentResource.GetCollectionDirectoryInfo(ancestors, serviceDirectory)
                                             .GetChildDirectory(name.ToString())
                                             .GetChildFile($"policy.xml");
            case IChildResource { Parent: var parent } childResource:
                if (parent is not IResourceWithDirectory parentResourceWithDirectory)
                {
                    throw new InvalidOperationException($"Expected policy '{name}' {ancestors.ToLogString()} to have a parent of type {nameof(IResourceWithDirectory)}.");
                }

                var parentAncestors = ResourceAncestors.From(ancestors.SkipLast(1));
                var parentName = ancestors.Last().Name;

                return parentResourceWithDirectory.GetCollectionDirectoryInfo(parentAncestors, serviceDirectory)
                                                  .GetChildDirectory(parentName.ToString())
                                                  .GetChildFile($"{name}.xml");
            default:
                return serviceDirectory.ToDirectoryInfo()
                                       .GetChildFile($"{name}.xml");
        }
    }

    public static Option<(ResourceName Name, ResourceAncestors Ancestors)> ParsePolicyFile(this IPolicyResource resource, FileInfo? file, ServiceDirectory serviceDirectory)
    {
        if (file is null)
        {
            return Option.None;
        }

        // Policy resources must be XML files
        if (Path.GetExtension(file.FullName) != ".xml")
        {
            return Option.None;
        }

        switch (resource)
        {
            case PolicyFragmentResource policyFragmentResource:
                return policyFragmentResource.Parse(file.Directory, serviceDirectory);
            case IChildResource childResource:
                if (childResource.Parent is not IResourceWithDirectory parentResourceWithDirectory)
                {
                    return Option.None;
                }

                return from parent in parentResourceWithDirectory.Parse(file.Directory, serviceDirectory)
                       let fileName = Path.GetFileNameWithoutExtension(file.Name)
                       from name in ResourceName.From(fileName).ToOption()
                       let ancestors = parent.Ancestors.Append(parentResourceWithDirectory, parent.Name)
                       select (name, ancestors);
            default:
                {
                    var fileName = Path.GetFileNameWithoutExtension(file.Name);

                    return from name in ResourceName.From(fileName).ToOption()
                           where file.Directory?.FullName == serviceDirectory.ToDirectoryInfo().FullName
                           select (name, ResourceAncestors.Empty);
                }
        }
    }

    public static async ValueTask<Option<JsonObject>> GetPolicyFileDto(this IPolicyResource resource, ResourceName name, ResourceAncestors ancestors, ServiceDirectory serviceDirectory, ReadFile readFile, CancellationToken cancellationToken)
    {
        var file = resource.GetPolicyFile(name, ancestors, serviceDirectory);

        return from contents in await readFile(file, cancellationToken)
               let dto = new PolicyDto
               {
                   Properties = new()
                   {
                       Format = "rawxml",
                       Value = contents.ToString()
                   }
               }
               from jsonObject in JsonObjectModule.From(dto, resource.SerializerOptions).ToOption()
               select jsonObject;
    }
}

public static class ResourceHttpExtensions
{
    public static IAsyncEnumerable<ResourceName> ListNames(this IResource resource, ResourceAncestors ancestors, ServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var uri = resource.GetCollectionUri(ancestors, serviceUri);

        return ListNames(uri, pipeline, cancellationToken);
    }

    private static IAsyncEnumerable<ResourceName> ListNames(Uri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri, cancellationToken)
                .Select(jsonObject => from name in jsonObject.GetStringProperty("name")
                                      from resourceName in ResourceName.From(name)
                                      select resourceName)
                .Choose(result => result.ToOption());

    private static Uri GetCollectionUri(this IResource resource, ResourceAncestors ancestors, ServiceUri serviceUri) =>
        ancestors.GetUri(serviceUri)
                 .AppendPathSegment(resource.CollectionUriPath)
                 .ToUri();

    private static Uri GetUri(this ResourceAncestors ancestors, ServiceUri serviceUri) =>
    ancestors.Aggregate(serviceUri.ToUri(),
                        (uri, ancestor) => uri.AppendPathSegments(ancestor.Resource.CollectionUriPath, ancestor.Name.ToString())
                                              .ToUri());

    public static IAsyncEnumerable<(ResourceName Name, JsonObject Dto)> ListNamesAndDtos(this IResourceWithDto resource, ResourceAncestors ancestors, ServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var uri = resource.GetCollectionUri(ancestors, serviceUri);

        uri = resource is IPolicyResource
                ? uri.AppendQueryParam("format", "rawxml")
                     .ToUri()
                : uri;

        return pipeline.ListJsonObjects(uri, cancellationToken)
                       .Select(jsonObject => from name in jsonObject.GetStringProperty("name")
                                             from resourceName in ResourceName.From(name)
                                             from normalizedDto in resource.DeserializeToDtoJson(jsonObject)
                                             select (resourceName, normalizedDto))
                       .Choose(result => result.ToOption());
    }

    // Normalizes JSON structure by deserializing to DTO type and re-serializing back to JsonObject.
    // This validates the JSON conforms to the DTO schema and removes any extraneous fields.
    private static Result<JsonObject> DeserializeToDtoJson(this IResourceWithDto resource, JsonNode dto)
    {
        try
        {
            var deserializedDto = dto.Deserialize(resource.DtoType, resource.SerializerOptions);
            var newNode = JsonSerializer.SerializeToNode(deserializedDto, resource.DtoType, resource.SerializerOptions);

            return newNode.AsJsonObject();
        }
        catch (JsonException exception)
        {
            return Error.From(exception);
        }
    }

    public static async ValueTask Delete(this IResource resource, ResourceName name, ResourceAncestors ancestors, ServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken, bool ignoreNotFound = false, bool waitForCompletion = true)
    {
        var uri = resource.GetUri(name, ancestors, serviceUri);
        var result = await pipeline.Delete(uri, cancellationToken, ignoreNotFound, waitForCompletion);
        result.IfErrorThrow();
    }

    public static Uri GetUri(this IResource resource, ResourceName name, ResourceAncestors ancestors, ServiceUri serviceUri) =>
        resource.GetCollectionUri(ancestors, serviceUri)
                .AppendPathSegment(name)
                .ToUri();

    public static async ValueTask<bool> IsSkuSupported(this IResource resource, ResourceAncestors ancestors, ServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        return await skuSupportCache.GetOrAdd((resource, serviceUri), _ => new(isSupported))
                                    .WithCancellation(cancellationToken);

        async Task<bool> isSupported(CancellationToken cancellationToken)
        {
            if (resource is ICompositeResource compositeResource)
            {
                return await isCompositeSupported(compositeResource, ancestors, serviceUri, pipeline, cancellationToken);
            }

            var uri = resource.GetCollectionUri(ancestors, serviceUri);
            var result = await pipeline.GetContent(uri, cancellationToken);

            return result.Map(_ => true)
                         .IfError(error => isUnsupportedSkuError(error) is false);
        }

        static async ValueTask<bool> isCompositeSupported(ICompositeResource resource, ResourceAncestors ancestors, ServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
        {
            var predecessorAncestors = ResourceAncestors.From(ancestors.SkipLast(1));

            return await resource.Primary.IsSkuSupported(predecessorAncestors, serviceUri, pipeline, cancellationToken)
                    && await resource.Secondary.IsSkuSupported(ancestors, serviceUri, pipeline, cancellationToken);
        }

        static bool isUnsupportedSkuError(Error error) =>
                error.ToException() is HttpRequestException httpRequestException
                && httpRequestException.StatusCode switch
                {
                    HttpStatusCode.BadRequest => httpRequestException.Message.Contains("MethodNotAllowedInPricingTier", StringComparison.OrdinalIgnoreCase),
                    HttpStatusCode.InternalServerError => httpRequestException.Message.Contains("Request processing failed due to internal error", StringComparison.OrdinalIgnoreCase),
                    _ => false
                };
    }

    private static readonly ConcurrentDictionary<(IResource, ServiceUri), AsyncLazy<bool>> skuSupportCache = new();

    public static async ValueTask<Result<JsonObject>> GetDto(this IResourceWithDto resource, ResourceName name, ResourceAncestors ancestors, ServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var uri = resource.GetUri(name, ancestors, serviceUri);

        uri = resource is IPolicyResource
                ? uri.AppendQueryParam("format", "rawxml")
                     .ToUri()
                : uri;

        return from content in await pipeline.GetContent(uri, cancellationToken)
               from dtoJson in resource.DeserializeToDtoJson(content)
               select dtoJson;
    }

    // Normalizes JSON structure by deserializing to DTO type and re-serializing back to JsonObject.
    // This validates the JSON conforms to the DTO schema and removes any extraneous fields.
    private static Result<JsonObject> DeserializeToDtoJson(this IResourceWithDto resource, BinaryData content) =>
        from node in JsonNodeModule.From(content)
        from normalizedNode in resource.DeserializeToDtoJson(node)
        select normalizedNode;

    public static async ValueTask PutDto(this IResourceWithDto resource, ResourceName name, JsonObject dto, ResourceAncestors ancestors, ServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var deleteAutomaticallyCreatedResources = resource is ApiResource or ProductResource;

        await resource.PutDto(name, dto, ancestors, serviceUri, pipeline, deleteAutomaticallyCreatedResources, cancellationToken);
    }

    private static async ValueTask PutDto(this IResourceWithDto resource, ResourceName name, JsonObject dto, ResourceAncestors ancestors, ServiceUri serviceUri, HttpPipeline pipeline, bool deleteAutomaticallyCreatedResources, CancellationToken cancellationToken)
    {
        var alreadyExists = false;
        if (deleteAutomaticallyCreatedResources)
        {
            alreadyExists = await resource.Exists(name, ancestors, serviceUri, pipeline, cancellationToken);
        }

        var uri = resource.GetUri(name, ancestors, serviceUri);
        var result = await pipeline.PutJson(uri, dto, cancellationToken);
        result.IfErrorThrow();

        if (deleteAutomaticallyCreatedResources && alreadyExists is false)
        {
            await resource.DeleteAutomaticallyCreatedResources(name, dto, ancestors, serviceUri, pipeline, cancellationToken);
        }
    }

    public static async ValueTask<bool> Exists(this IResource resource, ResourceName name, ResourceAncestors ancestors, ServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        switch (resource)
        {
            case ILinkResource linkResource:
                return await linkResource.Exists(name, ancestors, serviceUri, pipeline, cancellationToken);
            default:
                var uri = resource.GetUri(name, ancestors, serviceUri);

                var result = from option in await pipeline.Head(uri, cancellationToken)
                             select option.IsSome;

                return result.IfErrorThrow();
        }
    }

    public static async ValueTask<bool> Exists(this ILinkResource resource, ResourceName name, ResourceAncestors ancestors, ServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var uri = resource.GetUri(name, ancestors, serviceUri);

        var result = from option in await pipeline.GetOptionalContent(uri, cancellationToken)
                     select option.IsSome;

        return result.IfErrorThrow();
    }

    private static async ValueTask DeleteAutomaticallyCreatedResources(this IResourceWithDto resource, ResourceName name, JsonObject dto, ResourceAncestors ancestors, ServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await (resource switch
        {
            ApiResource apiResource => apiResource.DeleteAutomaticallyCreatedResources(name, dto, ancestors, serviceUri, pipeline, cancellationToken),
            ProductResource productResource => productResource.DeleteAutomaticallyCreatedResources(name, dto, ancestors, serviceUri, pipeline, cancellationToken),
            _ => ValueTask.CompletedTask
        });

    private static async ValueTask DeleteAutomaticallyCreatedResources(this ApiResource resource, ResourceName name, JsonObject dto, ResourceAncestors ancestors, ServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {

        var uri = resource.GetUri(name, ancestors, serviceUri);

        await deletePolicies(cancellationToken);
        await deleteDiagnostics(cancellationToken);
        await deleteProductApis(cancellationToken);

        async ValueTask deletePolicies(CancellationToken cancellationToken) =>
            await DeleteAutomaticallyCreatedResources(uri, "policies", pipeline, cancellationToken);

        async ValueTask deleteDiagnostics(CancellationToken cancellationToken) =>
            await DeleteAutomaticallyCreatedResources(uri, "diagnostics", pipeline, cancellationToken);

        async ValueTask deleteProductApis(CancellationToken cancellationToken)
        {
            var apiName = name;
            var productResource = ProductResource.Instance;

            await productResource.ListNames(ancestors, serviceUri, pipeline, cancellationToken)
                                 .IterTaskParallel(async productName =>
                                 {
                                     var productUri = productResource.GetUri(productName, ancestors, serviceUri)
                                                                     .AppendPathSegment("apis")
                                                                     .AppendQueryParam("$filter", $"name eq '{apiName}'")
                                                                     .ToUri();

                                     await DeleteAllNames(productUri, pipeline, cancellationToken);
                                 }, maxDegreeOfParallelism: Option.None, cancellationToken);
        }
    }

    private static async ValueTask DeleteAutomaticallyCreatedResources(this ProductResource resource, ResourceName name, JsonObject dto, ResourceAncestors ancestors, ServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var uri = resource.GetUri(name, ancestors, serviceUri);

        await deleteSubscriptions(cancellationToken);
        await deleteGroups(cancellationToken);

        async ValueTask deleteSubscriptions(CancellationToken cancellationToken)
        {
            if (await SubscriptionResource.Instance.IsSkuSupported(ancestors, serviceUri, pipeline, cancellationToken))
            {
                var subscriptionsUri = uri.AppendPathSegment("subscriptions").ToUri();

                await ListNames(subscriptionsUri, pipeline, cancellationToken)
                      .IterTaskParallel(async subscriptionName => await SubscriptionResource.Instance.Delete(subscriptionName, ancestors, serviceUri, pipeline, cancellationToken, ignoreNotFound: true, waitForCompletion: true),
                                        maxDegreeOfParallelism: Option.None,
                                        cancellationToken);
            }
        }

        async ValueTask deleteGroups(CancellationToken cancellationToken)
        {
            if (await GroupResource.Instance.IsSkuSupported(ancestors, serviceUri, pipeline, cancellationToken))
            {
                await DeleteAutomaticallyCreatedResources(uri, "groups", pipeline, cancellationToken);
            }
        }
    }

    private static async ValueTask DeleteAutomaticallyCreatedResources(Uri uri, string resourceSegment, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var childrenUri = uri.AppendPathSegment(resourceSegment).ToUri();

        await DeleteAllNames(childrenUri, pipeline, cancellationToken);
    }

    private static async ValueTask DeleteAllNames(Uri collectionUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await ListNames(collectionUri, pipeline, cancellationToken)
                .IterTaskParallel(async name =>
                {
                    var resourceUri = collectionUri.AppendPathSegment(name).ToUri();
                    await pipeline.Delete(resourceUri, cancellationToken, ignoreNotFound: true, waitForCompletion: true);
                }, maxDegreeOfParallelism: Option.None, cancellationToken);
}