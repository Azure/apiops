using Azure.Core.Pipeline;
using Flurl;
using LanguageExt;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public sealed record ApiReleaseName : ResourceName
{
    private ApiReleaseName(string value) : base(value) { }

    public static ApiReleaseName From(string value) => new(value);
}

public sealed record ApiReleasesUri : ResourceUri
{
    public required ApiUri Parent { get; init; }

    private static string PathSegment { get; } = "releases";

    protected override Uri Value => Parent.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static ApiReleasesUri From(ApiName name, ManagementServiceUri serviceUri) =>
        new() { Parent = ApiUri.From(name, serviceUri) };
}

public sealed record ApiReleaseUri : ResourceUri
{
    public required ApiReleasesUri Parent { get; init; }
    public required ApiReleaseName Name { get; init; }

    protected override Uri Value => Parent.ToUri().AppendPathSegment(Name.ToString()).ToUri();

    public static ApiReleaseUri From(ApiReleaseName name, ApiName apiName, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = ApiReleasesUri.From(apiName, serviceUri),
            Name = name
        };
}

public sealed record ApiReleasesDirectory : ResourceDirectory
{
    public required ApiDirectory Parent { get; init; }

    private static string Name { get; } = "releases";

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name);

    public static ApiReleasesDirectory From(ApiName name, ManagementServiceDirectory serviceDirectory) =>
        new() { Parent = ApiDirectory.From(name, serviceDirectory) };

    public static Option<ApiReleasesDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        directory is not null && directory.Name == Name
            ? from parent in ApiDirectory.TryParse(directory.Parent, serviceDirectory)
              select new ApiReleasesDirectory { Parent = parent }
            : Option<ApiReleasesDirectory>.None;
}

public sealed record ApiReleaseDirectory : ResourceDirectory
{
    public required ApiReleasesDirectory Parent { get; init; }

    public required ApiReleaseName Name { get; init; }

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name.ToString());

    public static ApiReleaseDirectory From(ApiReleaseName name, ApiName apiName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = ApiReleasesDirectory.From(apiName, serviceDirectory),
            Name = name
        };

    public static Option<ApiReleaseDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        from parent in ApiReleasesDirectory.TryParse(directory?.Parent, serviceDirectory)
        select new ApiReleaseDirectory
        {
            Parent = parent,
            Name = ApiReleaseName.From(directory!.Name)
        };
}

public sealed record ApiReleaseInformationFile : ResourceFile
{
    public required ApiReleaseDirectory Parent { get; init; }
    private static string Name { get; } = "apiReleaseInformation.json";

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile(Name);

    public static ApiReleaseInformationFile From(ApiReleaseName name, ApiName apiName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = ApiReleaseDirectory.From(name, apiName, serviceDirectory)
        };

    public static Option<ApiReleaseInformationFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        file is not null && file.Name == Name
            ? from parent in ApiReleaseDirectory.TryParse(file.Directory, serviceDirectory)
              select new ApiReleaseInformationFile { Parent = parent }
            : Option<ApiReleaseInformationFile>.None;
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

public static class ApiReleaseModule
{
    public static async ValueTask DeleteAll(this ApiReleasesUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await uri.ListNames(pipeline, cancellationToken)
                 .IterParallel(async name => await ApiReleaseUri.From(name, uri.Parent.Name, uri.Parent.Parent.ServiceUri)
                                                                .Delete(pipeline, cancellationToken),
                               cancellationToken);

    public static IAsyncEnumerable<ApiReleaseName> ListNames(this ApiReleasesUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(ApiReleaseName.From);

    public static IAsyncEnumerable<(ApiReleaseName Name, ApiReleaseDto Dto)> List(this ApiReleasesUri apiReleasesUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        apiReleasesUri.ListNames(pipeline, cancellationToken)
                      .SelectAwait(async name =>
                      {
                          var uri = ApiReleaseUri.From(name, apiReleasesUri.Parent.Name, apiReleasesUri.Parent.Parent.ServiceUri);
                          var dto = await uri.GetDto(pipeline, cancellationToken);
                          return (name, dto);
                      });

    public static async ValueTask<ApiReleaseDto> GetDto(this ApiReleaseUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = await pipeline.GetContent(uri.ToUri(), cancellationToken);
        return content.ToObjectFromJson<ApiReleaseDto>();
    }

    public static async ValueTask Delete(this ApiReleaseUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await pipeline.DeleteResource(uri.ToUri(), waitForCompletion: true, cancellationToken);

    public static async ValueTask PutDto(this ApiReleaseUri uri, ApiReleaseDto dto, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto);
        await pipeline.PutContent(uri.ToUri(), content, cancellationToken);
    }

    public static IEnumerable<ApiReleaseDirectory> ListDirectories(ApiName apiName, ManagementServiceDirectory serviceDirectory) =>
        ApiReleasesDirectory.From(apiName, serviceDirectory)
                            .ToDirectoryInfo()
                            .ListDirectories("*")
                            .Select(directory => ApiReleaseName.From(directory.Name))
                            .Select(releaseName => ApiReleaseDirectory.From(releaseName, apiName, serviceDirectory));

    public static IEnumerable<ApiReleaseDirectory> ListDirectories(ManagementServiceDirectory serviceDirectory) =>
        ApiModule.ListDirectories(serviceDirectory)
                 .Select(apiDirectory => apiDirectory.Name)
                 .SelectMany(apiName => ListDirectories(apiName, serviceDirectory));

    public static IEnumerable<ApiReleaseInformationFile> ListInformationFiles(ApiName apiName, ManagementServiceDirectory serviceDirectory) =>
        ListDirectories(apiName, serviceDirectory)
            .Select(directory => new ApiReleaseInformationFile { Parent = directory })
            .Where(informationFile => informationFile.ToFileInfo().Exists());

    public static IEnumerable<ApiReleaseInformationFile> ListInformationFiles(ManagementServiceDirectory serviceDirectory) =>
        ApiModule.ListDirectories(serviceDirectory)
                 .Select(apiDirectory => apiDirectory.Name)
                 .SelectMany(apiName => ListInformationFiles(apiName, serviceDirectory));

    public static async ValueTask WriteDto(this ApiReleaseInformationFile file, ApiReleaseDto dto, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto, JsonObjectExtensions.SerializerOptions);
        await file.ToFileInfo().OverwriteWithBinaryData(content, cancellationToken);
    }

    public static async ValueTask<ApiReleaseDto> ReadDto(this ApiReleaseInformationFile file, CancellationToken cancellationToken)
    {
        var content = await file.ToFileInfo().ReadAsBinaryData(cancellationToken);
        return content.ToObjectFromJson<ApiReleaseDto>();
    }
}