namespace common.codegen;

public interface IResource
{
    public string NameType { get; }
#pragma warning disable CA1056 // URI-like properties should not be strings
    public string CollectionUriType { get; }
    public string CollectionUriPath { get; }
    public string UriType { get; }
#pragma warning restore CA1056 // URI-like properties should not be strings
    public string ModuleType { get; }
}

public interface IChildResource : IResource
{
    public IResource Parent { get; }
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

public interface IResourceWithInformationFile : IResourceWithDto
{
    public string DirectoryType { get; }
    public string InformationFileType { get; }
    public string InformationFileName { get; }
}