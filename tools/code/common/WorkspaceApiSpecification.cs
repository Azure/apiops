using LanguageExt;
using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public abstract record WorkspaceApiSpecificationFile : ResourceFile
{
    public required WorkspaceApiDirectory Parent { get; init; }
    public abstract ApiSpecification Specification { get; }

    public static WorkspaceApiSpecificationFile From(ApiSpecification specification, WorkspaceApiName apiName, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        specification switch
        {
            ApiSpecification.GraphQl => WorkspaceGraphQlSpecificationFile.From(apiName, workspaceName, serviceDirectory),
            ApiSpecification.Wadl => WorkspaceWadlSpecificationFile.From(apiName, workspaceName, serviceDirectory),
            ApiSpecification.Wsdl => WorkspaceWsdlSpecificationFile.From(apiName, workspaceName, serviceDirectory),
            ApiSpecification.OpenApi openApi => WorkspaceOpenApiSpecificationFile.From(openApi, apiName, workspaceName, serviceDirectory),
            _ => throw new NotImplementedException()
        };

    public static async ValueTask<Option<WorkspaceApiSpecificationFile>> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await TryParse(file,
                       getFileContents: async file => await file.ReadAsBinaryData(cancellationToken),
                       serviceDirectory,
                       cancellationToken);

    public static async ValueTask<Option<WorkspaceApiSpecificationFile>> TryParse(FileInfo? file, Func<FileInfo, ValueTask<BinaryData>> getFileContents, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        var tryParseGraphQl = () => (from specificationFile in WorkspaceGraphQlSpecificationFile.TryParse(file, serviceDirectory)
                                     select specificationFile as WorkspaceApiSpecificationFile).AsTask();

        var tryParseWadl = () => (from specificationFile in WorkspaceWadlSpecificationFile.TryParse(file, serviceDirectory)
                                  select specificationFile as WorkspaceApiSpecificationFile).AsTask();

        var tryParseWsdl = () => (from specificationFile in WorkspaceWsdlSpecificationFile.TryParse(file, serviceDirectory)
                                  select specificationFile as WorkspaceApiSpecificationFile).AsTask();

        var tryParseOpenApi = async () => from specificationFile in await WorkspaceOpenApiSpecificationFile.TryParse(file, getFileContents, serviceDirectory, cancellationToken)
                                          select specificationFile as WorkspaceApiSpecificationFile;

        return await ImmutableArray.Create(tryParseGraphQl, tryParseWadl, tryParseWsdl, tryParseOpenApi)
                                   .Pick(async (f, cancellationToken) => await f(), cancellationToken);
    }
}

public sealed record WorkspaceGraphQlSpecificationFile : WorkspaceApiSpecificationFile
{
    public override ApiSpecification Specification { get; } = new ApiSpecification.GraphQl();

    public static string Name => "specification.graphql";

    protected override FileInfo Value => new(Path.Combine(Parent.ToDirectoryInfo().FullName, Name));

    public static WorkspaceGraphQlSpecificationFile From(WorkspaceApiName apiName, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new() { Parent = WorkspaceApiDirectory.From(apiName, workspaceName, serviceDirectory) };

    public static Option<WorkspaceGraphQlSpecificationFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        file is not null && file.Name == Name
            ? from parent in WorkspaceApiDirectory.TryParse(file.Directory, serviceDirectory)
              select new WorkspaceGraphQlSpecificationFile { Parent = parent }
            : Option<WorkspaceGraphQlSpecificationFile>.None;
}

public sealed record WorkspaceWadlSpecificationFile : WorkspaceApiSpecificationFile
{
    public override ApiSpecification Specification { get; } = new ApiSpecification.Wadl();

    public static string Name => "specification.wadl";

    protected override FileInfo Value => new(Path.Combine(Parent.ToDirectoryInfo().FullName, Name));

    public static WorkspaceWadlSpecificationFile From(WorkspaceApiName apiName, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new() { Parent = WorkspaceApiDirectory.From(apiName, workspaceName, serviceDirectory) };

    public static Option<WorkspaceWadlSpecificationFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        file is not null && file.Name == Name
            ? from parent in WorkspaceApiDirectory.TryParse(file.Directory, serviceDirectory)
              select new WorkspaceWadlSpecificationFile { Parent = parent }
            : Option<WorkspaceWadlSpecificationFile>.None;
}

public sealed record WorkspaceWsdlSpecificationFile : WorkspaceApiSpecificationFile
{
    public override ApiSpecification Specification { get; } = new ApiSpecification.Wsdl();

    public static string Name => "specification.wsdl";

    protected override FileInfo Value => new(Path.Combine(Parent.ToDirectoryInfo().FullName, Name));

    public static WorkspaceWsdlSpecificationFile From(WorkspaceApiName apiName, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new() { Parent = WorkspaceApiDirectory.From(apiName, workspaceName, serviceDirectory) };

    public static Option<WorkspaceWsdlSpecificationFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        file is not null && file.Name == Name
            ? from parent in WorkspaceApiDirectory.TryParse(file.Directory, serviceDirectory)
              select new WorkspaceWsdlSpecificationFile { Parent = parent }
            : Option<WorkspaceWsdlSpecificationFile>.None;
}

public abstract record WorkspaceOpenApiSpecificationFile : WorkspaceApiSpecificationFile
{
    public abstract OpenApiFormat Format { get; }
    public required OpenApiVersion Version { get; init; }

    public static WorkspaceOpenApiSpecificationFile From(ApiSpecification.OpenApi openApi, WorkspaceApiName apiName, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        openApi.Format switch
        {
            OpenApiFormat.Json => WorkspaceJsonOpenApiSpecificationFile.From(openApi.Version, apiName, workspaceName, serviceDirectory),
            OpenApiFormat.Yaml => WorkspaceYamlOpenApiSpecificationFile.From(openApi.Version, apiName, workspaceName, serviceDirectory),
            _ => throw new NotImplementedException()
        };

    public static new async ValueTask<Option<WorkspaceOpenApiSpecificationFile>> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await TryParse(file,
                       getFileContents: async file => await file.ReadAsBinaryData(cancellationToken),
                       serviceDirectory,
                       cancellationToken);

    public static new async ValueTask<Option<WorkspaceOpenApiSpecificationFile>> TryParse(FileInfo? file, Func<FileInfo, ValueTask<BinaryData>> getFileContents, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        var tryParseYaml = async () => from yaml in await WorkspaceYamlOpenApiSpecificationFile.TryParse(file, getFileContents, serviceDirectory, cancellationToken)
                                       select yaml as WorkspaceOpenApiSpecificationFile;

        var tryParseJson = async () => from json in await WorkspaceJsonOpenApiSpecificationFile.TryParse(file, getFileContents, serviceDirectory, cancellationToken)
                                       select json as WorkspaceOpenApiSpecificationFile;

        return await ImmutableArray.Create(tryParseYaml, tryParseJson)
                                   .Pick(async (f, cancellationToken) => await f(), cancellationToken);
    }
}

public sealed record WorkspaceYamlOpenApiSpecificationFile : WorkspaceOpenApiSpecificationFile
{
    public override OpenApiFormat Format { get; } = new OpenApiFormat.Yaml();
    public override ApiSpecification Specification => new ApiSpecification.OpenApi
    {
        Format = Format,
        Version = Version
    };
    public static string Name { get; } = "specification.yaml";

    protected override FileInfo Value => new(Path.Combine(Parent.ToDirectoryInfo().FullName, Name));

    public static WorkspaceYamlOpenApiSpecificationFile From(OpenApiVersion version, WorkspaceApiName apiName, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceApiDirectory.From(apiName, workspaceName, serviceDirectory),
            Version = version
        };

    public static new async ValueTask<Option<WorkspaceYamlOpenApiSpecificationFile>> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await TryParse(file,
                       getFileContents: async file => await file.ReadAsBinaryData(cancellationToken),
                       serviceDirectory,
                       cancellationToken);

    public static new async ValueTask<Option<WorkspaceYamlOpenApiSpecificationFile>> TryParse(FileInfo? file, Func<FileInfo, ValueTask<BinaryData>> getFileContents, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        file is not null && file.Name == Name
        ? await WorkspaceApiDirectory.TryParse(file.Directory, serviceDirectory)
                                     .BindTask(async parent => from version in await OpenApiVersion.TryParse(await getFileContents(file), cancellationToken)
                                                               select new WorkspaceYamlOpenApiSpecificationFile
                                                               {
                                                                   Parent = parent,
                                                                   Version = version
                                                               })
        : Option<WorkspaceYamlOpenApiSpecificationFile>.None;
}

public sealed record WorkspaceJsonOpenApiSpecificationFile : WorkspaceOpenApiSpecificationFile
{
    public override OpenApiFormat Format { get; } = new OpenApiFormat.Json();
    public override ApiSpecification Specification => new ApiSpecification.OpenApi
    {
        Format = Format,
        Version = Version
    };

    public static string Name { get; } = "specification.json";

    protected override FileInfo Value => new(Path.Combine(Parent.ToDirectoryInfo().FullName, Name));

    public static WorkspaceJsonOpenApiSpecificationFile From(OpenApiVersion version, WorkspaceApiName apiName, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceApiDirectory.From(apiName, workspaceName, serviceDirectory),
            Version = version
        };

    public static new async ValueTask<Option<WorkspaceJsonOpenApiSpecificationFile>> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await TryParse(file,
                       getFileContents: async file => await file.ReadAsBinaryData(cancellationToken),
                       serviceDirectory,
                       cancellationToken);

    public static new async ValueTask<Option<WorkspaceJsonOpenApiSpecificationFile>> TryParse(FileInfo? file, Func<FileInfo, ValueTask<BinaryData>> getFileContents, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        file is not null && file.Name == Name
        ? await WorkspaceApiDirectory.TryParse(file.Directory, serviceDirectory)
                                     .BindTask(async parent => from version in await OpenApiVersion.TryParse(await getFileContents(file), cancellationToken)
                                                               select new WorkspaceJsonOpenApiSpecificationFile
                                                               {
                                                                   Parent = parent,
                                                                   Version = version
                                                               })
        : Option<WorkspaceJsonOpenApiSpecificationFile>.None;
}

public static class WorkspaceApiSpecificationModule
{
    public static async ValueTask WriteSpecification(this WorkspaceApiSpecificationFile file, BinaryData contents, CancellationToken cancellationToken) =>
        await file.ToFileInfo().OverwriteWithBinaryData(contents, cancellationToken);
}