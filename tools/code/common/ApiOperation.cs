using Azure.Core.Pipeline;
using Flurl;
using LanguageExt;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace common;

public sealed record ApiOperationName : ResourceName
{
    private ApiOperationName(string value) : base(value) { }

    public static ApiOperationName From(string value) => new(value);
}

public sealed record ApiOperationsUri : ResourceUri
{
    public required ApiUri Parent { get; init; }

    private static string PathSegment { get; } = "operations";

    protected override Uri Value => Parent.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static ApiOperationsUri From(ApiName apiName, ManagementServiceUri serviceUri) =>
        new() { Parent = ApiUri.From(apiName, serviceUri) };
}

public sealed record ApiOperationUri : ResourceUri
{
    public required ApiOperationsUri Parent { get; init; }
    public required ApiOperationName Name { get; init; }

    protected override Uri Value => Parent.ToUri().AppendPathSegment(Name.ToString()).ToUri();

    public static ApiOperationUri From(ApiOperationName name, ApiName apiName, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = ApiOperationsUri.From(apiName, serviceUri),
            Name = name
        };
}

public sealed record ApiOperationsDirectory : ResourceDirectory
{
    public required ApiDirectory Parent { get; init; }

    private static string Name { get; } = "operations";

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name);

    public static ApiOperationsDirectory From(ApiName name, ManagementServiceDirectory serviceDirectory) =>
        new() { Parent = ApiDirectory.From(name, serviceDirectory) };

    public static Option<ApiOperationsDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        directory is not null &&
        directory.Name == Name
            ? from parent in ApiDirectory.TryParse(directory.Parent, serviceDirectory)
              select new ApiOperationsDirectory { Parent = parent }
            : Option<ApiOperationsDirectory>.None;
}

public sealed record ApiOperationDirectory : ResourceDirectory
{
    public required ApiOperationsDirectory Parent { get; init; }

    public required ApiOperationName Name { get; init; }

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name.ToString());

    public static ApiOperationDirectory From(ApiOperationName name, ApiName apiName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = ApiOperationsDirectory.From(apiName, serviceDirectory),
            Name = name
        };

    public static Option<ApiOperationDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        from name in TryParseApiOperationName(directory)
        from parent in ApiOperationsDirectory.TryParse(directory?.Parent, serviceDirectory)
        select new ApiOperationDirectory
        {
            Parent = parent,
            Name = name
        };

    internal static Option<ApiOperationName> TryParseApiOperationName(DirectoryInfo? directory) =>
        string.IsNullOrWhiteSpace(directory?.Name)
        ? Option<ApiOperationName>.None
        : ApiOperationName.From(directory.Name);
}

public static class ApiOperationModule
{
    public static IAsyncEnumerable<ApiOperationName> ListNames(this ApiOperationsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(ApiOperationName.From);

    public static IEnumerable<ApiOperationDirectory> ListDirectories(ApiName apiName, ManagementServiceDirectory serviceDirectory)
    {
        var parentDirectory = ApiOperationsDirectory.From(apiName, serviceDirectory);

        return parentDirectory.ToDirectoryInfo()
                              .ListDirectories("*")
                              .Choose(ApiOperationDirectory.TryParseApiOperationName)
                              .Select(apiOperationName => new ApiOperationDirectory
                              {
                                  Name = apiOperationName,
                                  Parent = parentDirectory
                              });
    }
}