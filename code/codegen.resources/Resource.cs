using System.Collections.Immutable;

namespace codegen.resources;

public interface IResource
{
    public string SingularDescription { get; }
    public string PluralDescription { get; }
    public string LoggerSingularDescription { get; }
    public string LoggerPluralDescription { get; }
#pragma warning disable CA1056 // URI-like properties should not be strings
    public string CollectionUriType { get; }
    public string CollectionUriPath { get; }
    public string UriType { get; }
#pragma warning restore CA1056 // URI-like properties should not be strings
}

public interface IChildResource : IResource
{
    public IResource Parent { get; }
}

public interface ICompositeResource : IResource
{
    public IResource First { get; }
    public IResource Second { get; }
}

public interface ILinkResource : ICompositeResource
{
    public string LinkName { get; }
}

public interface IPolicyResource : IResourceWithName, IResourceWithDto
{
    public string PolicyFileType { get; }
}

public interface IResourceWithName : IResource
{
    public string NameType { get; }
    public string NameParameter { get; }
}

public interface IResourceWithDirectory : IResource
{
    public string CollectionDirectoryType { get; }
    public string CollectionDirectoryName { get; }
    public string DirectoryType { get; }
}

public interface IResourceWithDto : IResource
{
    public string DtoType { get; }
    public string DtoCode { get; }
}

public interface IResourceWithInformationFile : IResourceWithDto, IResourceWithDirectory
{
    public string InformationFileType { get; }
    public string InformationFileName { get; }
}

public static class ResourceExtensions
{
    //public static ImmutableArray<string> GetSelfAndParentNameFunctionParameters(this IResource resource) =>
    //    resource.GetSelfAndParents()
    //            .Select(GetNameFunctionParameters)
    //            .ToImmutableArray();

    //public static ImmutableArray<string> GetParentNameFunctionParameters(this IResource resource) =>
    //    resource.GetParentsFromClosestToFarthest()
    //            .Select(GetNameFunctionParameters)
    //            .ToImmutableArray();

    //public static ImmutableArray<IResource> GetSelfAndParents(this IResource resource) =>
    //    [resource, .. resource.GetParentsFromClosestToFarthest()];

    //public static ImmutableArray<string> GetSelfAndParentNameFunctionArguments(this IResource resource) =>
    //    ImmutableArray.Create(resource)
    //                  .AddRange(resource.GetParentsFromClosestToFarthest())
    //                  .Select(GetNameFunctionArguments)
    //                  .ToImmutableArray();

    //public static ImmutableArray<string> GetParentNameFunctionArguments(this IResource resource) =>
    //    resource.GetParentsFromClosestToFarthest()
    //            .Select(GetNameFunctionArguments)
    //            .ToImmutableArray();

    public static ImmutableArray<IResource> GetResourceHierarchyBottomUp(this IResource resource) =>
        resource switch
        {
            ICompositeResource compositeResource => [resource, compositeResource.Second, compositeResource.First],
            IChildResource childResource => [childResource, .. childResource.Parent.GetResourceHierarchyBottomUp()],
            _ => [resource]
        };

    public static IResource? TryGetParent(this IResource resource) =>
        resource switch
        {
            IChildResource childResource => childResource.Parent,
            ICompositeResource compositeResource => compositeResource.Second,
            _ => null
        };

    //public static ImmutableArray<string> GetParentNameTypesBottomUp(this IResource resource) =>
    //    resource switch
    //    {
    //        IChildResource childResource =>
    //            childResource.Parent switch
    //            {
    //                IResourceWithName parent => [parent.NameType, .. parent.GetParentNameTypesBottomUp()],
    //                _ => []
    //            },
    //        ICompositeResource compositeResource =>
    //            (compositeResource.First, compositeResource.Second) switch
    //            {
    //                (IResourceWithName first, IResourceWithName second) => [second.NameType, first.NameType],
    //                _ => []
    //            },
    //        _ => []
    //    };

    private static ImmutableArray<IResource> GetResourceHierarchyTopToBottom(this IResource resource) =>
        resource switch
        {
            ILinkResource linkResource => [linkResource.First, linkResource.Second],
            IChildResource childResource => [.. childResource.Parent.GetResourceHierarchyTopToBottom(), childResource],
            _ => [resource]
        };

    private static ImmutableArray<IResource> GetParentsFromClosestToFarthest(this IChildResource resource) =>
        resource.Parent switch
        {
            IChildResource parent => [parent, .. parent.GetParentsFromClosestToFarthest()],
            var parent => [parent]
        };

    private static ImmutableArray<IResource> GetParents(this IChildResource resource) =>
        resource.Parent switch
        {
            IChildResource parent => [.. parent.GetParents(), parent],
            var parent => [parent]
        };

    //private static string GetNameFunctionParameters(this IResource resource) =>
    //    $"{resource.NameType} {resource.NameTypeCamelCase}";

    //private static string GetNameFunctionArguments(this IResource resource) =>
    //    resource.NameTypeCamelCase;

    public static string GetModuleClassName(this IResource resource) =>
        $"{resource.SingularDescription}Module";
}