using Azure.Core.Pipeline;
using Flurl;
using LanguageExt;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public sealed record LoggerName : ResourceName, IResourceName<LoggerName>
{
    private LoggerName(string value) : base(value) { }

    public static LoggerName From(string value) => new(value);
}

public sealed record LoggersUri : ResourceUri
{
    public required ManagementServiceUri ServiceUri { get; init; }

    private static string PathSegment { get; } = "loggers";

    protected override Uri Value => ServiceUri.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static LoggersUri From(ManagementServiceUri serviceUri) =>
        new() { ServiceUri = serviceUri };
}

public sealed record LoggerUri : ResourceUri
{
    public required LoggersUri Parent { get; init; }
    public required LoggerName Name { get; init; }

    protected override Uri Value => Parent.ToUri().AppendPathSegment(Name.ToString()).ToUri();

    public static LoggerUri From(LoggerName name, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = LoggersUri.From(serviceUri),
            Name = name
        };
}

public sealed record LoggersDirectory : ResourceDirectory
{
    public required ManagementServiceDirectory ServiceDirectory { get; init; }

    private static string Name { get; } = "loggers";

    protected override DirectoryInfo Value =>
        ServiceDirectory.ToDirectoryInfo().GetChildDirectory(Name);

    public static LoggersDirectory From(ManagementServiceDirectory serviceDirectory) =>
        new() { ServiceDirectory = serviceDirectory };

    public static Option<LoggersDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        directory is not null &&
        directory.Name == Name &&
        directory.Parent?.FullName == serviceDirectory.ToDirectoryInfo().FullName
            ? new LoggersDirectory { ServiceDirectory = serviceDirectory }
            : Option<LoggersDirectory>.None;
}

public sealed record LoggerDirectory : ResourceDirectory
{
    public required LoggersDirectory Parent { get; init; }

    public required LoggerName Name { get; init; }

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name.ToString());

    public static LoggerDirectory From(LoggerName name, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = LoggersDirectory.From(serviceDirectory),
            Name = name
        };

    public static Option<LoggerDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        from parent in LoggersDirectory.TryParse(directory?.Parent, serviceDirectory)
        select new LoggerDirectory
        {
            Parent = parent,
            Name = LoggerName.From(directory!.Name)
        };
}

public sealed record LoggerInformationFile : ResourceFile
{
    public required LoggerDirectory Parent { get; init; }
    private static string Name { get; } = "loggerInformation.json";

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile(Name);

    public static LoggerInformationFile From(LoggerName name, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = new LoggerDirectory
            {
                Parent = LoggersDirectory.From(serviceDirectory),
                Name = name
            }
        };

    public static Option<LoggerInformationFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        file is not null && file.Name == Name
            ? from parent in LoggerDirectory.TryParse(file.Directory, serviceDirectory)
              select new LoggerInformationFile { Parent = parent }
            : Option<LoggerInformationFile>.None;
}

public sealed record LoggerDto
{
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public required LoggerContract Properties { get; init; }

    public record LoggerContract
    {
        [JsonPropertyName("loggerType")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? LoggerType { get; init; }

        [JsonPropertyName("credentials")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public JsonObject? Credentials { get; init; }

        [JsonPropertyName("description")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Description { get; init; }

        [JsonPropertyName("isBuffered")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool? IsBuffered { get; init; }

        [JsonPropertyName("resourceId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? ResourceId { get; init; }
    }
}

public static class LoggerModule
{
    public static async ValueTask DeleteAll(this LoggersUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await uri.ListNames(pipeline, cancellationToken)
                 .IterParallel(async name => await LoggerUri.From(name, uri.ServiceUri)
                                                                .Delete(pipeline, cancellationToken),
                               cancellationToken);

    public static IAsyncEnumerable<LoggerName> ListNames(this LoggersUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(LoggerName.From);

    public static IAsyncEnumerable<(LoggerName Name, LoggerDto Dto)> List(this LoggersUri loggersUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        loggersUri.ListNames(pipeline, cancellationToken)
                      .SelectAwait(async name =>
                      {
                          var uri = new LoggerUri { Parent = loggersUri, Name = name };
                          var dto = await uri.GetDto(pipeline, cancellationToken);
                          return (name, dto);
                      });

    public static async ValueTask<Option<LoggerDto>> TryGetDto(this LoggerUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var contentOption = await pipeline.GetContentOption(uri.ToUri(), cancellationToken);
        return contentOption.Map(content => content.ToObjectFromJson<LoggerDto>());
    }

    public static async ValueTask<LoggerDto> GetDto(this LoggerUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = await pipeline.GetContent(uri.ToUri(), cancellationToken);
        return content.ToObjectFromJson<LoggerDto>();
    }

    public static async ValueTask Delete(this LoggerUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await pipeline.DeleteResource(uri.ToUri(), waitForCompletion: true, cancellationToken);

    public static async ValueTask PutDto(this LoggerUri uri, LoggerDto dto, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto);
        await pipeline.PutContent(uri.ToUri(), content, cancellationToken);
    }

    public static IEnumerable<LoggerDirectory> ListDirectories(ManagementServiceDirectory serviceDirectory)
    {
        var loggersDirectory = LoggersDirectory.From(serviceDirectory);

        return loggersDirectory.ToDirectoryInfo()
                            .ListDirectories("*")
                            .Select(directoryInfo => LoggerName.From(directoryInfo.Name))
                            .Select(name => new LoggerDirectory { Parent = loggersDirectory, Name = name });
    }

    public static IEnumerable<LoggerInformationFile> ListInformationFiles(ManagementServiceDirectory serviceDirectory) =>
        ListDirectories(serviceDirectory)
            .Select(directory => new LoggerInformationFile { Parent = directory })
            .Where(informationFile => informationFile.ToFileInfo().Exists());

    public static async ValueTask WriteDto(this LoggerInformationFile file, LoggerDto dto, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto, JsonObjectExtensions.SerializerOptions);
        await file.ToFileInfo().OverwriteWithBinaryData(content, cancellationToken);
    }

    public static async ValueTask<LoggerDto> ReadDto(this LoggerInformationFile file, CancellationToken cancellationToken)
    {
        var content = await file.ToFileInfo().ReadAsBinaryData(cancellationToken);
        return content.ToObjectFromJson<LoggerDto>();
    }
}