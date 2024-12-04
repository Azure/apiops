using LanguageExt;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Readers;
using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public abstract record ApiSpecification
{
    public sealed record GraphQl : ApiSpecification;
    public sealed record Wadl : ApiSpecification;
    public sealed record Wsdl : ApiSpecification;
    public sealed record OpenApi : ApiSpecification
    {
        public required OpenApiVersion Version { get; init; }
        public required OpenApiFormat Format { get; init; }
    }
}

public abstract record OpenApiVersion
{
    public sealed record V2 : OpenApiVersion;
    public sealed record V3 : OpenApiVersion;

    public static async ValueTask<Option<OpenApiVersion>> Parse(BinaryData data, CancellationToken cancellationToken)
    {
#pragma warning disable CA1031 // Do not catch general exception types
        try
        {
            using var stream = data.ToStream();
            var reader = new OpenApiStreamReader();
            var result = await reader.ReadAsync(stream, cancellationToken);

            return result.OpenApiDiagnostic.SpecificationVersion switch
            {
                OpenApiSpecVersion.OpenApi2_0 => new OpenApiVersion.V2(),
                OpenApiSpecVersion.OpenApi3_0 => new OpenApiVersion.V3(),
                _ => Option<OpenApiVersion>.None
            };
        }
        catch (Exception)
        {
            return Option<OpenApiVersion>.None;
        }
#pragma warning restore CA1031 // Do not catch general exception types
    }
}

public abstract record OpenApiFormat
{
    public sealed record Json : OpenApiFormat;
    public sealed record Yaml : OpenApiFormat;
}

public abstract record ApiSpecificationFile : ResourceFile
{
    protected ApiSpecificationFile(string path) : base(path) { }

    public abstract ApiDirectory Parent { get; }
    public abstract ApiSpecification Specification { get; }

    public static ApiSpecificationFile From(ApiSpecification specification, ApiName apiName, ServiceDirectory serviceDirectory) =>
        specification switch
        {
            ApiSpecification.GraphQl => GraphQlSpecificationFile.From(apiName, serviceDirectory),
            ApiSpecification.Wadl => WadlSpecificationFile.From(apiName, serviceDirectory),
            ApiSpecification.Wsdl => WsdlSpecificationFile.From(apiName, serviceDirectory),
            ApiSpecification.OpenApi openApi => OpenApiSpecificationFile.From(openApi, apiName, serviceDirectory),
            _ => throw new NotImplementedException()
        };

    public static async ValueTask<Option<ApiSpecificationFile>> Parse(FileInfo? file, ServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await Parse(file,
                    getFileContents: async file => await file.ReadAsBinaryData(cancellationToken),
                    serviceDirectory,
                    cancellationToken);

    public static async ValueTask<Option<ApiSpecificationFile>> Parse(FileInfo? file, Func<FileInfo, ValueTask<BinaryData>> getFileContents, ServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        var parseGraphQl = () => (from specificationFile in GraphQlSpecificationFile.Parse(file, serviceDirectory)
                                  select specificationFile as ApiSpecificationFile).AsTask();

        var parseWadl = () => (from specificationFile in WadlSpecificationFile.Parse(file, serviceDirectory)
                               select specificationFile as ApiSpecificationFile).AsTask();

        var parseWsdl = () => (from specificationFile in WsdlSpecificationFile.Parse(file, serviceDirectory)
                               select specificationFile as ApiSpecificationFile).AsTask();

        var parseOpenApi = async () => from specificationFile in await OpenApiSpecificationFile.Parse(file, getFileContents, serviceDirectory, cancellationToken)
                                       select specificationFile as ApiSpecificationFile;

        return await ImmutableArray.Create(parseGraphQl, parseWadl, parseWsdl, parseOpenApi)
                                   .Pick(async (f, cancellationToken) => await f(), cancellationToken);
    }
}

public sealed record GraphQlSpecificationFile : ApiSpecificationFile
{
    private GraphQlSpecificationFile(ApiDirectory parent) : base(Path.Combine(parent.ToDirectoryInfo().FullName, Name)) =>
        Parent = parent;

    public override ApiDirectory Parent { get; }

    public override ApiSpecification Specification { get; } = new ApiSpecification.GraphQl();

    public const string Name = "specification.graphql";

    public static GraphQlSpecificationFile From(ApiName apiName, ServiceDirectory serviceDirectory) =>
        new(ApiDirectory.From(apiName, serviceDirectory));

    public static Option<GraphQlSpecificationFile> Parse(FileInfo? file, ServiceDirectory serviceDirectory) =>
        file switch
        {
            { Name: Name } => from parent in ApiDirectory.Parse(file.Directory, serviceDirectory)
                              select new GraphQlSpecificationFile(parent),
            _ => Option<GraphQlSpecificationFile>.None
        };
}

public sealed record WadlSpecificationFile : ApiSpecificationFile
{
    private WadlSpecificationFile(ApiDirectory parent) : base(Path.Combine(parent.ToDirectoryInfo().FullName, Name)) =>
        Parent = parent;

    public override ApiDirectory Parent { get; }

    public override ApiSpecification Specification { get; } = new ApiSpecification.Wadl();

    public const string Name = "specification.wadl";

    public static WadlSpecificationFile From(ApiName apiName, ServiceDirectory serviceDirectory) =>
        new(ApiDirectory.From(apiName, serviceDirectory));

    public static Option<WadlSpecificationFile> Parse(FileInfo? file, ServiceDirectory serviceDirectory) =>
        file switch
        {
            { Name: Name } => from parent in ApiDirectory.Parse(file.Directory, serviceDirectory)
                              select new WadlSpecificationFile(parent),
            _ => Option<WadlSpecificationFile>.None
        };
}

public sealed record WsdlSpecificationFile : ApiSpecificationFile
{
    private WsdlSpecificationFile(ApiDirectory parent) : base(Path.Combine(parent.ToDirectoryInfo().FullName, Name)) =>
        Parent = parent;

    public override ApiDirectory Parent { get; }

    public override ApiSpecification Specification { get; } = new ApiSpecification.Wsdl();

    public const string Name = "specification.wsdl";

    public static WsdlSpecificationFile From(ApiName apiName, ServiceDirectory serviceDirectory) =>
        new(ApiDirectory.From(apiName, serviceDirectory));

    public static Option<WsdlSpecificationFile> Parse(FileInfo? file, ServiceDirectory serviceDirectory) =>
        file switch
        {
            { Name: Name } => from parent in ApiDirectory.Parse(file.Directory, serviceDirectory)
                              select new WsdlSpecificationFile(parent),
            _ => Option<WsdlSpecificationFile>.None
        };
}

public abstract record OpenApiSpecificationFile : ApiSpecificationFile
{
    protected OpenApiSpecificationFile(string path) : base(path) { }

    public abstract OpenApiFormat Format { get; }
    public abstract OpenApiVersion Version { get; }

    public static OpenApiSpecificationFile From(ApiSpecification.OpenApi openApi, ApiName apiName, ServiceDirectory serviceDirectory) =>
        openApi.Format switch
        {
            OpenApiFormat.Json => JsonOpenApiSpecificationFile.From(openApi.Version, apiName, serviceDirectory),
            OpenApiFormat.Yaml => YamlOpenApiSpecificationFile.From(openApi.Version, apiName, serviceDirectory),
            _ => throw new NotImplementedException()
        };

    public static new async ValueTask<Option<OpenApiSpecificationFile>> Parse(FileInfo? file, ServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await Parse(file,
                    getFileContents: async file => await file.ReadAsBinaryData(cancellationToken),
                    serviceDirectory,
                    cancellationToken);

    public static new async ValueTask<Option<OpenApiSpecificationFile>> Parse(FileInfo? file, Func<FileInfo, ValueTask<BinaryData>> getFileContents, ServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        var parseYaml = async () => from yaml in await YamlOpenApiSpecificationFile.Parse(file, getFileContents, serviceDirectory, cancellationToken)
                                    select yaml as OpenApiSpecificationFile;

        var parseJson = async () => from json in await JsonOpenApiSpecificationFile.Parse(file, getFileContents, serviceDirectory, cancellationToken)
                                    select json as OpenApiSpecificationFile;

        return await ImmutableArray.Create(parseYaml, parseJson)
                                   .Pick(async (f, cancellationToken) => await f(), cancellationToken);
    }
}

public sealed record YamlOpenApiSpecificationFile : OpenApiSpecificationFile
{
    private YamlOpenApiSpecificationFile(OpenApiVersion version, ApiDirectory parent) : base(Path.Combine(parent.ToDirectoryInfo().FullName, Name))
    {
        Version = version;
        Parent = parent;
    }

    public override ApiDirectory Parent { get; }
    public override OpenApiFormat Format { get; } = new OpenApiFormat.Yaml();
    public override OpenApiVersion Version { get; }
    public override ApiSpecification Specification => new ApiSpecification.OpenApi
    {
        Format = Format,
        Version = Version
    };

    public const string Name = "specification.yaml";

    public static YamlOpenApiSpecificationFile From(OpenApiVersion version, ApiName apiName, ServiceDirectory serviceDirectory) =>
        new(version, ApiDirectory.From(apiName, serviceDirectory));

    public static new async ValueTask<Option<YamlOpenApiSpecificationFile>> Parse(FileInfo? file, ServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await Parse(file,
                    getFileContents: async file => await file.ReadAsBinaryData(cancellationToken),
                    serviceDirectory,
                    cancellationToken);

    public static new async ValueTask<Option<YamlOpenApiSpecificationFile>> Parse(FileInfo? file, Func<FileInfo, ValueTask<BinaryData>> getFileContents, ServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        file switch
        {
            { Name: Name } => await ApiDirectory.Parse(file.Directory, serviceDirectory)
                                                .BindTask(async directory =>
                                                {
                                                    var contents = await getFileContents(file);
                                                    var versionOption = await OpenApiVersion.Parse(contents, cancellationToken);

                                                    return from version in versionOption
                                                           select new YamlOpenApiSpecificationFile(version, directory);
                                                }),
            _ => Option<YamlOpenApiSpecificationFile>.None
        };
}

public sealed record JsonOpenApiSpecificationFile : OpenApiSpecificationFile
{
    private JsonOpenApiSpecificationFile(OpenApiVersion version, ApiDirectory parent) : base(Path.Combine(parent.ToDirectoryInfo().FullName, Name))
    {
        Version = version;
        Parent = parent;
    }

    public override ApiDirectory Parent { get; }
    public override OpenApiFormat Format { get; } = new OpenApiFormat.Json();
    public override OpenApiVersion Version { get; }
    public override ApiSpecification Specification => new ApiSpecification.OpenApi
    {
        Format = Format,
        Version = Version
    };

    public const string Name = "specification.json";

    public static JsonOpenApiSpecificationFile From(OpenApiVersion version, ApiName apiName, ServiceDirectory serviceDirectory) =>
        new(version, ApiDirectory.From(apiName, serviceDirectory));

    public static new async ValueTask<Option<JsonOpenApiSpecificationFile>> Parse(FileInfo? file, ServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await Parse(file,
                    getFileContents: async file => await file.ReadAsBinaryData(cancellationToken),
                    serviceDirectory,
                    cancellationToken);

    public static new async ValueTask<Option<JsonOpenApiSpecificationFile>> Parse(FileInfo? file, Func<FileInfo, ValueTask<BinaryData>> getFileContents, ServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        file switch
        {
            { Name: Name } => await ApiDirectory.Parse(file.Directory, serviceDirectory)
                                                .BindTask(async directory =>
                                                {
                                                    var contents = await getFileContents(file);
                                                    var versionOption = await OpenApiVersion.Parse(contents, cancellationToken);

                                                    return from version in versionOption
                                                           select new JsonOpenApiSpecificationFile(version, directory);
                                                }),
            _ => Option<JsonOpenApiSpecificationFile>.None
        };
}

public static class ApiSpecificationModule
{
    public static async ValueTask Write(this ApiSpecificationFile file, BinaryData contents, CancellationToken cancellationToken) =>
        await file.ToFileInfo().OverwriteWithBinaryData(contents, cancellationToken);
}