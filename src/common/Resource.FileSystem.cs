using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public delegate ValueTask<Option<JsonObject>> GetInformationFileDto(IResourceWithInformationFile resource, ResourceName name, ParentChain parents, ReadFile readFile, GetSubDirectories getSubDirectories, CancellationToken cancellationToken);
public delegate ValueTask<Option<BinaryData>> GetPolicyFileContents(IPolicyResource resource, ResourceName name, ParentChain parents, ReadFile readFile, CancellationToken cancellationToken);
public delegate ValueTask WriteInformationFile(IResourceWithInformationFile resource, ResourceName name, JsonObject dto, ParentChain parents, CancellationToken cancellationToken);
public delegate ValueTask WritePolicyFile(IPolicyResource resource, ResourceName name, JsonObject dto, ParentChain parents, CancellationToken cancellationToken);
public delegate ValueTask<Option<ResourceKey>> ParseResourceFile(FileInfo file, ReadFile readFile, CancellationToken cancellationToken);

public static partial class ResourceModule
{
    public static void ConfigureGetInformationFileDto(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureServiceDirectory(builder);
        builder.TryAddSingleton(ResolveGetInformationFileDto);
    }

    private static GetInformationFileDto ResolveGetInformationFileDto(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ServiceDirectory>();

        return async (resource, name, parents, readFile, getSubDirectories, cancellationToken) =>
        {
            switch (resource)
            {
                case ILinkResource linkResource:
                    return await getLinkResourceDto(linkResource, name, parents, getSubDirectories, readFile, cancellationToken);
                default:
                    var file = resource.GetInformationFile(name, parents, serviceDirectory);

                    return from contents in await readFile(file, cancellationToken)
                           from jsonObject in JsonObjectModule.From(contents, resource.SerializerOptions)
                                                              .ToOption()
                           select jsonObject;
            }
        };

        async ValueTask<Option<JsonObject>> getLinkResourceDto(ILinkResource resource, ResourceName name, ParentChain parents, GetSubDirectories getSubDirectories, ReadFile readFile, CancellationToken cancellationToken)
        {
            var collectionDirectory = resource.GetCollectionDirectoryInfo(parents, serviceDirectory);

            return await getSubDirectories(collectionDirectory)
                            .BindTask(async subDirectories => await parseDirectories(subDirectories, cancellationToken));

            async ValueTask<Option<JsonObject>> parseDirectories(IEnumerable<DirectoryInfo> directories, CancellationToken cancellationToken) =>
                await directories.Choose(parseDirectory)
                                 .ToArrayAsync(cancellationToken) switch
                {
                    [] => Option.None,
                    [var single] => single,
                    var many => throw new InvalidOperationException($"Found multiple matches for '{new ResourceKey
                    {
                        Name = name,
                        Resource = resource,
                        Parents = parents
                    }}.")
                };

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
    }

    private static FileInfo GetInformationFile(this IResourceWithInformationFile resource, ResourceName name, ParentChain parents, ServiceDirectory serviceDirectory) =>
       resource switch
       {
           ILinkResource linkResource => throw new InvalidOperationException($"We don't have enough information to get the file for link resources. Use the {nameof(ILinkResource)} extension that takes a {nameof(JsonObject)} DTO parameter."),
           _ => resource.GetCollectionDirectoryInfo(parents, serviceDirectory)
                        .GetChildDirectory(name.ToString())
                        .GetChildFile(resource.FileName)
       };

    /// <remarks>
    /// The directory name is derived from the linked resource ID in the DTO.
    /// </remarks>
    private static Result<FileInfo> GetInformationFile(this ILinkResource resource, JsonObject dto, ParentChain parents, ServiceDirectory serviceDirectory) =>
       from properties in dto.GetJsonObjectProperty("properties")
       from id in properties.GetStringProperty(resource.DtoPropertyNameForLinkedResource)
                            .MapError(error => $"Could not find '{resource.DtoPropertyNameForLinkedResource}' in DTO for resource {resource.SingularName} with parents {parents.ToResourceId()}. {error}")
       let secondaryResourceName = id.Split('/').Last()
       select resource.GetCollectionDirectoryInfo(parents, serviceDirectory)
                      .GetChildDirectory(secondaryResourceName)
                      .GetChildFile(resource.FileName);

    public static void ConfigureGetPolicyFileContents(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureServiceDirectory(builder);
        builder.TryAddSingleton(ResolveGetPolicyFileContents);
    }

    private static GetPolicyFileContents ResolveGetPolicyFileContents(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ServiceDirectory>();

        return async (resource, name, parents, readFile, cancellationToken) =>
        {
            var file = resource.GetPolicyFile(name, parents, serviceDirectory);
            return await readFile(file, cancellationToken);
        };
    }

    private static FileInfo GetPolicyFile(this IPolicyResource resource, ResourceName name, ParentChain parents, ServiceDirectory serviceDirectory)
    {
        switch (resource)
        {
            case PolicyFragmentResource policyFragmentResource:
                return policyFragmentResource.GetCollectionDirectoryInfo(parents, serviceDirectory)
                                             .GetChildDirectory(name.ToString())
                                             .GetChildFile($"policy.xml");
            case WorkspacePolicyFragmentResource workspacePolicyFragmentResource:
                return workspacePolicyFragmentResource.GetCollectionDirectoryInfo(parents, serviceDirectory)
                                                      .GetChildDirectory(name.ToString())
                                                      .GetChildFile($"policy.xml");
            case IChildResource { Parent: var parent } childResource:
                if (parent is not IResourceWithDirectory parentResourceWithDirectory)
                {
                    throw new InvalidOperationException($"Expected policy '{new ResourceKey
                    {
                        Name = name,
                        Resource = resource,
                        Parents = parents
                    }} to have a parent of type {nameof(IResourceWithDirectory)}.");
                }

                var parentParents = ParentChain.From(parents.SkipLast(1));
                var parentName = parents.Last().Name;

                return parentResourceWithDirectory.GetCollectionDirectoryInfo(parentParents, serviceDirectory)
                                                  .GetChildDirectory(parentName.ToString())
                                                  .GetChildFile($"{name}.xml");
            default:
                return serviceDirectory.ToDirectoryInfo()
                                       .GetChildFile($"{name}.xml");
        }
    }

    public static void ConfigureWriteInformationFile(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureServiceDirectory(builder);

        builder.TryAddSingleton(ResolveWriteInformationFile);
    }

    private static WriteInformationFile ResolveWriteInformationFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ServiceDirectory>();

        return async (resource, name, dto, parents, cancellationToken) =>
        {
            var file = resource switch
            {
                ILinkResource linkResource => linkResource.GetInformationFile(dto, parents, serviceDirectory)
                                                          .IfErrorThrow(),
                _ => resource.GetInformationFile(name, parents, serviceDirectory)
            };

            var formattedDto = resource.FormatInformationFileDto(dto);

            await file.OverwriteWithJson(formattedDto, cancellationToken);
        };
    }

    private static JsonObject FormatInformationFileDto(this IResourceWithInformationFile resource, JsonObject dto)
    {
        var formattedDto = dto;

        if (resource is PolicyFragmentResource policyFragmentResource)
        {
            formattedDto = policyFragmentResource.FormatInformationFileDto(formattedDto);
        }

        if (resource is WorkspacePolicyFragmentResource workspacePolicyFragmentResource)
        {
            formattedDto = workspacePolicyFragmentResource.FormatInformationFileDto(formattedDto);
        }

        if (resource is ILinkResource linkResource)
        {
            var name = dto.GetStringProperty("name")
                          .Bind(ResourceName.From)
                          .IfErrorThrow();

            formattedDto = linkResource.FormatInformationFileDto(name, formattedDto);
        }

        if (resource is IResourceWithReference withReference)
        {
            formattedDto = withReference.FormatInformationFileDto(formattedDto);
        }

        if (resource is ApiResource apiResource)
        {
            formattedDto = apiResource.FormatInformationFileDto(formattedDto);
        }

        if (resource is WorkspaceApiResource workspaceApiResource)
        {
            formattedDto = workspaceApiResource.FormatInformationFileDto(formattedDto);
        }

        return formattedDto;
    }

    /// <summary>
    /// Transform the absolute resource ID in the link property to a relative ID and ensure the DTO contains the resource name.
    /// </summary>
    private static JsonObject FormatInformationFileDto(this ILinkResource resource, ResourceName name, JsonObject dto)
    {
        // Transform the absolute resource ID in the link property to a relative ID
        var updatedDto = (resource.Primary, resource.Secondary) switch
        {
            // Workspace product groups don't support relative IDs
            (WorkspaceProductResource, WorkspaceGroupResource) => dto,
            // Workspace product APIs don't support relative IDs
            (WorkspaceProductResource, WorkspaceApiResource) => dto,
            _ => SetAbsoluteToRelativeId(dto, resource.DtoPropertyNameForLinkedResource)
        };

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

    /// <summary>
    /// Transform absolute resource IDs in <paramref name="dto"/>'s properties.<paramref name="idPropertyName"/> to relative IDs.
    /// </summary>
    /// <param name="dto"></param>
    /// <param name="idPropertyName"></param>
    /// <returns></returns>
    private static JsonObject SetAbsoluteToRelativeId(JsonObject dto, string idPropertyName)
    {
        var updatedDto = from properties in dto.GetJsonObjectProperty("properties")
                         from id in properties.GetStringProperty(idPropertyName)
                         let formattedId = SetAbsoluteToRelativeId(id)
                         let updatedProperties = properties.SetProperty(idPropertyName, formattedId)
                         select dto.SetProperty("properties", updatedProperties);

        return updatedDto.IfError(_ => dto);
    }

    /// <summary>
    /// Transforms an absolute resource ID to a relative ID that is not tied to a specific service.
    /// For example, "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg1/providers/Microsoft.ApiManagement/service/apimService1/loggers/azuremonitor"
    /// becomes "/loggers/azuremonitor".
    /// </summary>
    private static string SetAbsoluteToRelativeId(string absoluteResourceId)
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

    private static DirectoryInfo GetCollectionDirectoryInfo(this IResourceWithDirectory resource, ParentChain parents, ServiceDirectory serviceDirectory) =>
        parents.Aggregate(serviceDirectory.ToDirectoryInfo(),
                          (directory, parent) => parent.Resource switch
                          {
                              IResourceWithDirectory parentResource =>
                                directory.GetChildDirectory(parentResource.CollectionDirectoryName)
                                         .GetChildDirectory(parent.Name.ToString()),
                              _ => throw new InvalidOperationException($"Parent resource '{parent.Name}' of type {parent.Resource.GetType()} is not an {nameof(IResourceWithDirectory)}.")
                          })
                .GetChildDirectory(resource.CollectionDirectoryName);

    public static void ConfigureWritePolicyFile(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureServiceDirectory(builder);

        builder.TryAddSingleton(ResolveWritePolicyFile);
    }

    private static WritePolicyFile ResolveWritePolicyFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ServiceDirectory>();

        return async (resource, name, dto, parents, cancellationToken) =>
        {
            var contentsResult = from properties in dto.GetJsonObjectProperty("properties")
                                 from value in properties.GetStringProperty("value")
                                 select BinaryData.FromString(value);

            var contents = contentsResult.IfErrorThrow();

            var file = resource.GetPolicyFile(name, parents, serviceDirectory);

            await file.OverwriteWithBinaryData(contents, cancellationToken);
        };
    }

    public static void ConfigureParseResourceFile(IHostApplicationBuilder builder)
    {
        ResourceGraphModule.ConfigureResourceGraph(builder);
        ManagementServiceModule.ConfigureServiceDirectory(builder);
        builder.TryAddSingleton(ResolveParseResourceFile);
    }

    private static ParseResourceFile ResolveParseResourceFile(IServiceProvider provider)
    {
        var graph = provider.GetRequiredService<ResourceGraph>();
        var serviceDirectory = provider.GetRequiredService<ServiceDirectory>();

        return async (file, readFile, cancellationToken) =>
        {
            var matches = await graph.TopologicallySortedResources
                                     .Choose(async resource => from x in await parseResource(resource, file, readFile, cancellationToken)
                                                               select new ResourceKey
                                                               {
                                                                   Resource = resource,
                                                                   Name = x.Name,
                                                                   Parents = x.Parents
                                                               })
                                     .ToArrayAsync(cancellationToken);

            return matches switch
            {
                [] => Option.None,
                [var match] => match,
                _ => throw new InvalidOperationException($"Multiple resources matched the file '{file.FullName}': {string.Join(", ", matches)}."),
            };
        };

        async ValueTask<Option<(ResourceName Name, ParentChain Parents)>> parseResource(IResource resource, FileInfo? file, ReadFile readFile, CancellationToken cancellationToken)
        {
            var option = Option<(ResourceName name, ParentChain parents)>.None();

            if (resource is ILinkResource linkResource)
            {
                option = await linkResource.ParseFile(file, readFile, serviceDirectory, cancellationToken);
            }

            if (resource is IResourceWithInformationFile resourceWithInformationFile and not ILinkResource)
            {
                option = option.IfNone(() => resourceWithInformationFile.ParseFile(file, serviceDirectory));
            }

            if (resource is IPolicyResource policyResource)
            {
                option = option.IfNone(() => policyResource.ParseFile(file, serviceDirectory));
            }

            if (resource is ApiResource apiResource)
            {
                option = option.IfNone(() => apiResource.ParseSpecificationFile(file, serviceDirectory));
            }

            if (resource is WorkspaceApiResource workspaceApiResource)
            {
                option = option.IfNone(() => workspaceApiResource.ParseSpecificationFile(file, serviceDirectory));
            }

            return option;
        }
    }

    private static async ValueTask<Option<(ResourceName Name, ParentChain Parents)>> ParseFile(this ILinkResource resource, FileInfo? file, ReadFile readFile, ServiceDirectory serviceDirectory, CancellationToken cancellationToken)
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
               from parents in resource.ParseParents(file.Directory, serviceDirectory)
               from dto in JsonObjectModule.From(contents, resource.SerializerOptions).ToOption()
               from expectedFile in resource.GetInformationFile(dto, parents, serviceDirectory).ToOption()
               where expectedFile.FullName == file.FullName
               from name in parseLinkResourceName(resource, contents)
               select (name, parents);

        static Option<ResourceName> parseLinkResourceName(ILinkResource resource, BinaryData contents)
        {
            var result = from jsonObject in JsonObjectModule.From(contents, resource.SerializerOptions)
                         from nameString in jsonObject.GetStringProperty("name")
                         from name in ResourceName.From(nameString)
                         select name;

            return result.ToOption();
        }
    }

    private static Option<ParentChain> ParseParents(this IResourceWithDirectory resource, DirectoryInfo? directory, ServiceDirectory serviceDirectory) =>
               resource.GetTraversalPredecessor()
                       .Match(// Predecessor must be an IResourceWithDirectory. If it is, parse it and get its parent chain.
                              predecessor => predecessor is IResourceWithDirectory predecessorResourceWithDirectory
                                               ? from parent in predecessorResourceWithDirectory.ParseDirectory(directory?.Parent?.Parent, serviceDirectory)
                                                 select parent.Parents.Append(predecessor, parent.Name)
                                               : Option.None,
                              // Resource has no predecessor. Make sure the path is correct relative to the service directory.
                              () => serviceDirectory.ToDirectoryInfo().FullName == directory?.Parent?.Parent?.FullName
                                       ? Option.Some(ParentChain.Empty)
                                       : Option.None);

    private static Option<(ResourceName Name, ParentChain Parents)> ParseDirectory(this IResourceWithDirectory resource, DirectoryInfo? directory, ServiceDirectory serviceDirectory)
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

        return from parents in resource.ParseParents(directory, serviceDirectory)
               from name in ResourceName.From(directory.Name).ToOption()
               select (name, parents);
    }

    private static Option<(ResourceName Name, ParentChain Parents)> ParseFile(this IResourceWithInformationFile resource, FileInfo? file, ServiceDirectory serviceDirectory)
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

        return resource.ParseDirectory(file.Directory, serviceDirectory);
    }

    private static Option<(ResourceName Name, ParentChain Parents)> ParseFile(this IPolicyResource resource, FileInfo? file, ServiceDirectory serviceDirectory)
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
                return policyFragmentResource.ParseDirectory(file.Directory, serviceDirectory);
            case WorkspacePolicyFragmentResource workspacePolicyFragmentResource:
                return workspacePolicyFragmentResource.ParseDirectory(file.Directory, serviceDirectory);
            case IChildResource childResource:
                if (childResource.Parent is not IResourceWithDirectory parentResourceWithDirectory)
                {
                    return Option.None;
                }

                return from parent in parentResourceWithDirectory.ParseDirectory(file.Directory, serviceDirectory)
                       let fileName = Path.GetFileNameWithoutExtension(file.Name)
                       from name in ResourceName.From(fileName).ToOption()
                       let parents = parent.Parents.Append(parentResourceWithDirectory, parent.Name)
                       select (name, parents);
            default:
                {
                    var fileName = Path.GetFileNameWithoutExtension(file.Name);

                    return from name in ResourceName.From(fileName).ToOption()
                           where file.Directory?.FullName == serviceDirectory.ToDirectoryInfo().FullName
                           select (name, ParentChain.Empty);
                }
        }
    }
}

