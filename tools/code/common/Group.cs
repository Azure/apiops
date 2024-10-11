using Azure.Core.Pipeline;
using Flurl;
using LanguageExt;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public sealed record GroupName : ResourceName, IResourceName<GroupName>
{
    private GroupName(string value) : base(value) { }

    public static GroupName From(string value) => new(value);
}

public sealed record GroupsUri : ResourceUri
{
    public required ManagementServiceUri ServiceUri { get; init; }

    private static string PathSegment { get; } = "groups";

    protected override Uri Value => ServiceUri.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static GroupsUri From(ManagementServiceUri serviceUri) =>
        new() { ServiceUri = serviceUri };
}

public sealed record GroupUri : ResourceUri
{
    public required GroupsUri Parent { get; init; }
    public required GroupName Name { get; init; }

    protected override Uri Value => Parent.ToUri().AppendPathSegment(Name.ToString()).ToUri();

    public static GroupUri From(GroupName name, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = GroupsUri.From(serviceUri),
            Name = name
        };
}

public sealed record GroupsDirectory : ResourceDirectory
{
    public required ManagementServiceDirectory ServiceDirectory { get; init; }

    private static string Name { get; } = "groups";

    protected override DirectoryInfo Value =>
        ServiceDirectory.ToDirectoryInfo().GetChildDirectory(Name);

    public static GroupsDirectory From(ManagementServiceDirectory serviceDirectory) =>
        new() { ServiceDirectory = serviceDirectory };

    public static Option<GroupsDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        directory is not null &&
        directory.Name == Name &&
        directory.Parent?.FullName == serviceDirectory.ToDirectoryInfo().FullName
            ? new GroupsDirectory { ServiceDirectory = serviceDirectory }
            : Option<GroupsDirectory>.None;
}

public sealed record GroupDirectory : ResourceDirectory
{
    public required GroupsDirectory Parent { get; init; }

    public required GroupName Name { get; init; }

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name.ToString());

    public static GroupDirectory From(GroupName name, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = GroupsDirectory.From(serviceDirectory),
            Name = name
        };

    public static Option<GroupDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        from parent in GroupsDirectory.TryParse(directory?.Parent, serviceDirectory)
        select new GroupDirectory
        {
            Parent = parent,
            Name = GroupName.From(directory!.Name)
        };
}

public sealed record GroupInformationFile : ResourceFile
{
    public required GroupDirectory Parent { get; init; }
    private static string Name { get; } = "groupInformation.json";

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile(Name);

    public static GroupInformationFile From(GroupName name, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = new GroupDirectory
            {
                Parent = GroupsDirectory.From(serviceDirectory),
                Name = name
            }
        };

    public static Option<GroupInformationFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        file is not null && file.Name == Name
            ? from parent in GroupDirectory.TryParse(file.Directory, serviceDirectory)
              select new GroupInformationFile { Parent = parent }
            : Option<GroupInformationFile>.None;
}

public sealed record GroupDto
{
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public required GroupContract Properties { get; init; }

    public sealed record GroupContract
    {
        [JsonPropertyName("displayName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? DisplayName { get; init; }

        [JsonPropertyName("description")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Description { get; init; }

        [JsonPropertyName("externalId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? ExternalId { get; init; }

        [JsonPropertyName("type")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Type { get; init; }
    }
}
public static class GroupModule
{
    public static async ValueTask DeleteAll(this GroupsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await uri.ListNames(pipeline, cancellationToken)
                 .IterParallel(async name => await GroupUri.From(name, uri.ServiceUri)
                                                                .Delete(pipeline, cancellationToken),
                               cancellationToken);

    public static IAsyncEnumerable<GroupName> ListNames(this GroupsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var exceptionHandler = (HttpRequestException exception) =>
            exception.StatusCode == HttpStatusCode.BadRequest
             && exception.Message.Contains("MethodNotAllowedInPricingTier", StringComparison.OrdinalIgnoreCase)
            ? AsyncEnumerable.Empty<GroupName>()
            : throw exception;

        return pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(GroupName.From)
                .Catch(exceptionHandler);
    }

    public static IAsyncEnumerable<(GroupName Name, GroupDto Dto)> List(this GroupsUri groupsUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        groupsUri.ListNames(pipeline, cancellationToken)
                 .SelectAwait(async name =>
                 {
                     var uri = new GroupUri { Parent = groupsUri, Name = name };
                     var dto = await uri.GetDto(pipeline, cancellationToken);
                     return (name, dto);
                 });

    public static async ValueTask<Option<GroupDto>> TryGetDto(this GroupUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var contentOption = await pipeline.GetContentOption(uri.ToUri(), cancellationToken);
        return contentOption.Map(content => content.ToObjectFromJson<GroupDto>());
    }

    public static async ValueTask<GroupDto> GetDto(this GroupUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = await pipeline.GetContent(uri.ToUri(), cancellationToken);
        return content.ToObjectFromJson<GroupDto>();
    }

    public static async ValueTask Delete(this GroupUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var either = await pipeline.TryDeleteResource(uri.ToUri(), waitForCompletion: true, cancellationToken);

        // Don't throw if we get an error saying system groups cannot be deleted.
        _ = either.IfLeft(response => response.Status == (int)HttpStatusCode.MethodNotAllowed
                                      && response.Content.ToString().Contains("System entity cannot be deleted", StringComparison.OrdinalIgnoreCase)
                                        ? Unit.Default
                                        : throw response.ToHttpRequestException(uri.ToUri()));
    }

    public static async ValueTask PutDto(this GroupUri uri, GroupDto dto, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto);
        await pipeline.PutContent(uri.ToUri(), content, cancellationToken);
    }

    public static IEnumerable<GroupDirectory> ListDirectories(ManagementServiceDirectory serviceDirectory)
    {
        var groupsDirectory = GroupsDirectory.From(serviceDirectory);

        return groupsDirectory.ToDirectoryInfo()
                              .ListDirectories("*")
                              .Select(directoryInfo => GroupName.From(directoryInfo.Name))
                              .Select(name => new GroupDirectory { Parent = groupsDirectory, Name = name });
    }

    public static IEnumerable<GroupInformationFile> ListInformationFiles(ManagementServiceDirectory serviceDirectory) =>
        ListDirectories(serviceDirectory)
            .Select(directory => new GroupInformationFile { Parent = directory })
            .Where(informationFile => informationFile.ToFileInfo().Exists());

    public static async ValueTask WriteDto(this GroupInformationFile file, GroupDto dto, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto, JsonObjectExtensions.SerializerOptions);
        await file.ToFileInfo().OverwriteWithBinaryData(content, cancellationToken);
    }

    public static async ValueTask<GroupDto> ReadDto(this GroupInformationFile file, CancellationToken cancellationToken)
    {
        var content = await file.ToFileInfo().ReadAsBinaryData(cancellationToken);
        return content.ToObjectFromJson<GroupDto>();
    }
}