using Azure.Core.Pipeline;
using Flurl;
using LanguageExt;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public sealed record GatewayApisUri : ResourceUri
{
    public required GatewayUri Parent { get; init; }

    private static string PathSegment { get; } = "apis";

    protected override Uri Value => Parent.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static GatewayApisUri From(GatewayName name, ManagementServiceUri serviceUri) =>
        new() { Parent = GatewayUri.From(name, serviceUri) };
}

public sealed record GatewayApiUri : ResourceUri
{
    public required GatewayApisUri Parent { get; init; }
    public required ApiName Name { get; init; }

    protected override Uri Value => Parent.ToUri().AppendPathSegment(Name.ToString()).ToUri();

    public static GatewayApiUri From(ApiName name, GatewayName gatewayName, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = GatewayApisUri.From(gatewayName, serviceUri),
            Name = name
        };
}

public sealed record GatewayApisDirectory : ResourceDirectory
{
    public required GatewayDirectory Parent { get; init; }
    private static string Name { get; } = "apis";

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name);

    public static GatewayApisDirectory From(GatewayName name, ManagementServiceDirectory serviceDirectory) =>
        new() { Parent = GatewayDirectory.From(name, serviceDirectory) };

    public static Option<GatewayApisDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        IsDirectoryNameValid(directory)
            ? from parent in GatewayDirectory.TryParse(directory.Parent, serviceDirectory)
              select new GatewayApisDirectory { Parent = parent }
            : Option<GatewayApisDirectory>.None;

    internal static bool IsDirectoryNameValid([NotNullWhen(true)] DirectoryInfo? directory) =>
        directory?.Name == Name;
}

public sealed record GatewayApiDirectory : ResourceDirectory
{
    public required GatewayApisDirectory Parent { get; init; }

    public required ApiName Name { get; init; }

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name.ToString());

    public static GatewayApiDirectory From(ApiName name, GatewayName gatewayName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = GatewayApisDirectory.From(gatewayName, serviceDirectory),
            Name = name
        };

    public static Option<GatewayApiDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        from name in TryParseGatewayApiName(directory)
        from parent in GatewayApisDirectory.TryParse(directory?.Parent, serviceDirectory)
        select new GatewayApiDirectory
        {
            Parent = parent,
            Name = name
        };

    internal static Option<ApiName> TryParseGatewayApiName(DirectoryInfo? directory) =>
        string.IsNullOrWhiteSpace(directory?.Name)
        ? Option<ApiName>.None
        : ApiName.From(directory.Name);
}

public sealed record GatewayApiInformationFile : ResourceFile
{
    public required GatewayApiDirectory Parent { get; init; }

    private static string Name { get; } = "gatewayApiInformation.json";

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile(Name);

    public static GatewayApiInformationFile From(ApiName name, GatewayName gatewayName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = GatewayApiDirectory.From(name, gatewayName, serviceDirectory)
        };

    public static Option<GatewayApiInformationFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        IsFileNameValid(file)
        ? from parent in GatewayApiDirectory.TryParse(file.Directory, serviceDirectory)
          select new GatewayApiInformationFile { Parent = parent }
          : Option<GatewayApiInformationFile>.None;

    internal static bool IsFileNameValid([NotNullWhen(true)] FileInfo? file) =>
        file?.Name == Name;
}

public sealed record GatewayApiDto
{
    public static GatewayApiDto Instance { get; } = new();
}

public static class GatewayApiModule
{
    public static IAsyncEnumerable<ApiName> ListNames(this GatewayApisUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(ApiName.From)
                .Where(ApiName.IsNotRevisioned);

    public static IAsyncEnumerable<(ApiName Name, GatewayApiDto Dto)> List(this GatewayApisUri gatewayApisUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        gatewayApisUri.ListNames(pipeline, cancellationToken)
                        .Select(name => (name, GatewayApiDto.Instance));

    public static async ValueTask Delete(this GatewayApiUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await pipeline.DeleteResource(uri.ToUri(), waitForCompletion: true, cancellationToken);

    public static async ValueTask PutDto(this GatewayApiUri uri, GatewayApiDto dto, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto);
        await pipeline.PutContent(uri.ToUri(), content, cancellationToken);
    }

    public static IEnumerable<GatewayApiInformationFile> ListInformationFiles(GatewayName gatewayName, ManagementServiceDirectory serviceDirectory) =>
        ListGatewayApisDirectories(gatewayName, serviceDirectory)
            .SelectMany(ListGatewayApiDirectories)
            .Select(directory => GatewayApiInformationFile.From(directory.Name, gatewayName, serviceDirectory));

    private static IEnumerable<GatewayApisDirectory> ListGatewayApisDirectories(GatewayName gatewayName, ManagementServiceDirectory serviceDirectory) =>
        GatewayDirectory.From(gatewayName, serviceDirectory)
                        .ToDirectoryInfo()
                        .ListDirectories("*")
                        .Where(GatewayApisDirectory.IsDirectoryNameValid)
                        .Select(_ => GatewayApisDirectory.From(gatewayName, serviceDirectory));

    private static IEnumerable<GatewayApiDirectory> ListGatewayApiDirectories(GatewayApisDirectory gatewayApisDirectory) =>
        gatewayApisDirectory.ToDirectoryInfo()
                              .ListDirectories("*")
                              .Choose(directory => from name in GatewayApiDirectory.TryParseGatewayApiName(directory)
                                                   select new GatewayApiDirectory { Name = name, Parent = gatewayApisDirectory });

    public static async ValueTask WriteDto(this GatewayApiInformationFile file, GatewayApiDto dto, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto, JsonObjectExtensions.SerializerOptions);
        await file.ToFileInfo().OverwriteWithBinaryData(content, cancellationToken);
    }

    public static async ValueTask<GatewayApiDto> ReadDto(this GatewayApiInformationFile file, CancellationToken cancellationToken)
    {
        var content = await file.ToFileInfo().ReadAsBinaryData(cancellationToken);
        return content.ToObjectFromJson<GatewayApiDto>();
    }
}