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

public sealed record GatewayName : ResourceName, IResourceName<GatewayName>
{
    private GatewayName(string value) : base(value) { }

    public static GatewayName From(string value) => new(value);
}

public sealed record GatewaysUri : ResourceUri
{
    public required ManagementServiceUri ServiceUri { get; init; }

    private static string PathSegment { get; } = "gateways";

    protected override Uri Value => ServiceUri.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static GatewaysUri From(ManagementServiceUri serviceUri) =>
        new() { ServiceUri = serviceUri };
}

public sealed record GatewayUri : ResourceUri
{
    public required GatewaysUri Parent { get; init; }
    public required GatewayName Name { get; init; }

    protected override Uri Value => Parent.ToUri().AppendPathSegment(Name.ToString()).ToUri();

    public static GatewayUri From(GatewayName name, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = GatewaysUri.From(serviceUri),
            Name = name
        };
}

public sealed record GatewaysDirectory : ResourceDirectory
{
    public required ManagementServiceDirectory ServiceDirectory { get; init; }

    private static string Name { get; } = "gateways";

    protected override DirectoryInfo Value =>
        ServiceDirectory.ToDirectoryInfo().GetChildDirectory(Name);

    public static GatewaysDirectory From(ManagementServiceDirectory serviceDirectory) =>
        new() { ServiceDirectory = serviceDirectory };

    public static Option<GatewaysDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        directory is not null &&
        directory.Name == Name &&
        directory.Parent?.FullName == serviceDirectory.ToDirectoryInfo().FullName
            ? new GatewaysDirectory { ServiceDirectory = serviceDirectory }
            : Option<GatewaysDirectory>.None;
}

public sealed record GatewayDirectory : ResourceDirectory
{
    public required GatewaysDirectory Parent { get; init; }

    public required GatewayName Name { get; init; }

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name.ToString());

    public static GatewayDirectory From(GatewayName name, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = GatewaysDirectory.From(serviceDirectory),
            Name = name
        };

    public static Option<GatewayDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        from parent in GatewaysDirectory.TryParse(directory?.Parent, serviceDirectory)
        select new GatewayDirectory
        {
            Parent = parent,
            Name = GatewayName.From(directory!.Name)
        };
}

public sealed record GatewayInformationFile : ResourceFile
{
    public required GatewayDirectory Parent { get; init; }
    private static string Name { get; } = "gatewayInformation.json";

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile(Name);

    public static GatewayInformationFile From(GatewayName name, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = new GatewayDirectory
            {
                Parent = GatewaysDirectory.From(serviceDirectory),
                Name = name
            }
        };

    public static Option<GatewayInformationFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        file is not null && file.Name == Name
            ? from parent in GatewayDirectory.TryParse(file.Directory, serviceDirectory)
              select new GatewayInformationFile { Parent = parent }
            : Option<GatewayInformationFile>.None;
}

public sealed record GatewayDto
{
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public required GatewayContract Properties { get; init; }

    public sealed record GatewayContract
    {
        [JsonPropertyName("description")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Description { get; init; }

        [JsonPropertyName("locationData")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public ResourceLocationDataContract? LocationData { get; init; }
    }

    public sealed record ResourceLocationDataContract
    {
        [JsonPropertyName("city")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? City { get; init; }

        [JsonPropertyName("countryOrRegion")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? CountryOrRegion { get; init; }

        [JsonPropertyName("district")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? District { get; init; }

        [JsonPropertyName("name")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Name { get; init; }
    }
}

public static class GatewayModule
{
    public static async ValueTask DeleteAll(this GatewaysUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await uri.ListNames(pipeline, cancellationToken)
                 .IterParallel(async name => await GatewayUri.From(name, uri.ServiceUri)
                                                                .Delete(pipeline, cancellationToken),
                               cancellationToken);

    public static IAsyncEnumerable<GatewayName> ListNames(this GatewaysUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var exceptionHandler = (HttpRequestException exception) =>
            exception.StatusCode == HttpStatusCode.BadRequest
             && exception.Message.Contains("MethodNotAllowedInPricingTier", StringComparison.OrdinalIgnoreCase)
            ? AsyncEnumerable.Empty<GatewayName>()
            : throw exception;

        return pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                       .Select(jsonObject => jsonObject.GetStringProperty("name"))
                       .Select(GatewayName.From)
                       .Catch(exceptionHandler);
    }

    public static IAsyncEnumerable<(GatewayName Name, GatewayDto Dto)> List(this GatewaysUri gatewaysUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        gatewaysUri.ListNames(pipeline, cancellationToken)
                      .SelectAwait(async name =>
                      {
                          var uri = new GatewayUri { Parent = gatewaysUri, Name = name };
                          var dto = await uri.GetDto(pipeline, cancellationToken);
                          return (name, dto);
                      });

    public static async ValueTask<Option<GatewayDto>> TryGetDto(this GatewayUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var contentOption = await pipeline.GetContentOption(uri.ToUri(), cancellationToken);
        return contentOption.Map(content => content.ToObjectFromJson<GatewayDto>());
    }

    public static async ValueTask<GatewayDto> GetDto(this GatewayUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = await pipeline.GetContent(uri.ToUri(), cancellationToken);
        return content.ToObjectFromJson<GatewayDto>();
    }

    public static async ValueTask Delete(this GatewayUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await pipeline.DeleteResource(uri.ToUri(), waitForCompletion: true, cancellationToken);

    public static async ValueTask PutDto(this GatewayUri uri, GatewayDto dto, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto);
        await pipeline.PutContent(uri.ToUri(), content, cancellationToken);
    }

    public static IEnumerable<GatewayDirectory> ListDirectories(ManagementServiceDirectory serviceDirectory)
    {
        var gatewaysDirectory = GatewaysDirectory.From(serviceDirectory);

        return gatewaysDirectory.ToDirectoryInfo()
                                   .ListDirectories("*")
                                   .Select(directoryInfo => GatewayName.From(directoryInfo.Name))
                                   .Select(name => new GatewayDirectory { Parent = gatewaysDirectory, Name = name });
    }

    public static IEnumerable<GatewayInformationFile> ListInformationFiles(ManagementServiceDirectory serviceDirectory) =>
        ListDirectories(serviceDirectory)
            .Select(directory => new GatewayInformationFile { Parent = directory })
            .Where(informationFile => informationFile.ToFileInfo().Exists());

    public static async ValueTask WriteDto(this GatewayInformationFile file, GatewayDto dto, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto, JsonObjectExtensions.SerializerOptions);
        await file.ToFileInfo().OverwriteWithBinaryData(content, cancellationToken);
    }

    public static async ValueTask<GatewayDto> ReadDto(this GatewayInformationFile file, CancellationToken cancellationToken)
    {
        var content = await file.ToFileInfo().ReadAsBinaryData(cancellationToken);
        return content.ToObjectFromJson<GatewayDto>();
    }
}