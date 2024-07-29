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

    public static async ValueTask<Option<OpenApiVersion>> TryParse(BinaryData data, CancellationToken cancellationToken)
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
    public required ApiDirectory Parent { get; init; }
    public abstract ApiSpecification Specification { get; }

    public static ApiSpecificationFile From(ApiSpecification specification, ApiName apiName, ManagementServiceDirectory serviceDirectory) =>
        specification switch
        {
            ApiSpecification.GraphQl => GraphQlSpecificationFile.From(apiName, serviceDirectory),
            ApiSpecification.Wadl => WadlSpecificationFile.From(apiName, serviceDirectory),
            ApiSpecification.Wsdl => WsdlSpecificationFile.From(apiName, serviceDirectory),
            ApiSpecification.OpenApi openApi => OpenApiSpecificationFile.From(openApi, apiName, serviceDirectory),
            _ => throw new NotImplementedException()
        };

    public static async ValueTask<Option<ApiSpecificationFile>> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await TryParse(file,
                       getFileContents: async file => await file.ReadAsBinaryData(cancellationToken),
                       serviceDirectory,
                       cancellationToken);

    public static async ValueTask<Option<ApiSpecificationFile>> TryParse(FileInfo? file, Func<FileInfo, ValueTask<BinaryData>> getFileContents, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        var tryParseGraphQl = () => (from specificationFile in GraphQlSpecificationFile.TryParse(file, serviceDirectory)
                                     select specificationFile as ApiSpecificationFile).AsTask();

        var tryParseWadl = () => (from specificationFile in WadlSpecificationFile.TryParse(file, serviceDirectory)
                                  select specificationFile as ApiSpecificationFile).AsTask();

        var tryParseWsdl = () => (from specificationFile in WsdlSpecificationFile.TryParse(file, serviceDirectory)
                                  select specificationFile as ApiSpecificationFile).AsTask();

        var tryParseOpenApi = async () => from specificationFile in await OpenApiSpecificationFile.TryParse(file, getFileContents, serviceDirectory, cancellationToken)
                                          select specificationFile as ApiSpecificationFile;

        return await ImmutableArray.Create(tryParseGraphQl, tryParseWadl, tryParseWsdl, tryParseOpenApi)
                                   .Pick(async (f, cancellationToken) => await f(), cancellationToken);
    }
}

public sealed record GraphQlSpecificationFile : ApiSpecificationFile
{
    public override ApiSpecification Specification { get; } = new ApiSpecification.GraphQl();

    public static string Name => "specification.graphql";

    protected override FileInfo Value => new(Path.Combine(Parent.ToDirectoryInfo().FullName, Name));

    public static GraphQlSpecificationFile From(ApiName apiName, ManagementServiceDirectory serviceDirectory) =>
        new() { Parent = ApiDirectory.From(apiName, serviceDirectory) };

    public static Option<GraphQlSpecificationFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        file is not null && file.Name == Name
            ? from parent in ApiDirectory.TryParse(file.Directory, serviceDirectory)
              select new GraphQlSpecificationFile { Parent = parent }
            : Option<GraphQlSpecificationFile>.None;
}

public sealed record WadlSpecificationFile : ApiSpecificationFile
{
    public override ApiSpecification Specification { get; } = new ApiSpecification.Wadl();

    public static string Name => "specification.wadl";

    protected override FileInfo Value => new(Path.Combine(Parent.ToDirectoryInfo().FullName, Name));

    public static WadlSpecificationFile From(ApiName apiName, ManagementServiceDirectory serviceDirectory) =>
        new() { Parent = ApiDirectory.From(apiName, serviceDirectory) };

    public static Option<WadlSpecificationFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        file is not null && file.Name == Name
            ? from parent in ApiDirectory.TryParse(file.Directory, serviceDirectory)
              select new WadlSpecificationFile { Parent = parent }
            : Option<WadlSpecificationFile>.None;
}

public sealed record WsdlSpecificationFile : ApiSpecificationFile
{
    public override ApiSpecification Specification { get; } = new ApiSpecification.Wsdl();

    public static string Name => "specification.wsdl";

    protected override FileInfo Value => new(Path.Combine(Parent.ToDirectoryInfo().FullName, Name));

    public static WsdlSpecificationFile From(ApiName apiName, ManagementServiceDirectory serviceDirectory) =>
        new() { Parent = ApiDirectory.From(apiName, serviceDirectory) };

    public static Option<WsdlSpecificationFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        file is not null && file.Name == Name
            ? from parent in ApiDirectory.TryParse(file.Directory, serviceDirectory)
              select new WsdlSpecificationFile { Parent = parent }
            : Option<WsdlSpecificationFile>.None;
}

public abstract record OpenApiSpecificationFile : ApiSpecificationFile
{
    public abstract OpenApiFormat Format { get; }
    public required OpenApiVersion Version { get; init; }

    public static OpenApiSpecificationFile From(ApiSpecification.OpenApi openApi, ApiName apiName, ManagementServiceDirectory serviceDirectory) =>
        openApi.Format switch
        {
            OpenApiFormat.Json => JsonOpenApiSpecificationFile.From(openApi.Version, apiName, serviceDirectory),
            OpenApiFormat.Yaml => YamlOpenApiSpecificationFile.From(openApi.Version, apiName, serviceDirectory),
            _ => throw new NotImplementedException()
        };

    public static new async ValueTask<Option<OpenApiSpecificationFile>> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await TryParse(file,
                       getFileContents: async file => await file.ReadAsBinaryData(cancellationToken),
                       serviceDirectory,
                       cancellationToken);

    public static new async ValueTask<Option<OpenApiSpecificationFile>> TryParse(FileInfo? file, Func<FileInfo, ValueTask<BinaryData>> getFileContents, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        var tryParseYaml = async () => from yaml in await YamlOpenApiSpecificationFile.TryParse(file, getFileContents, serviceDirectory, cancellationToken)
                                       select yaml as OpenApiSpecificationFile;

        var tryParseJson = async () => from json in await JsonOpenApiSpecificationFile.TryParse(file, getFileContents, serviceDirectory, cancellationToken)
                                       select json as OpenApiSpecificationFile;

        return await ImmutableArray.Create(tryParseYaml, tryParseJson)
                                   .Pick(async (f, cancellationToken) => await f(), cancellationToken);
    }
}

public sealed record YamlOpenApiSpecificationFile : OpenApiSpecificationFile
{
    public override OpenApiFormat Format { get; } = new OpenApiFormat.Yaml();
    public override ApiSpecification Specification => new ApiSpecification.OpenApi
    {
        Format = Format,
        Version = Version
    };
    public static string Name { get; } = "specification.yaml";

    protected override FileInfo Value => new(Path.Combine(Parent.ToDirectoryInfo().FullName, Name));

    public static YamlOpenApiSpecificationFile From(OpenApiVersion version, ApiName apiName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = ApiDirectory.From(apiName, serviceDirectory),
            Version = version
        };

    public static new async ValueTask<Option<YamlOpenApiSpecificationFile>> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await TryParse(file,
                       getFileContents: async file => await file.ReadAsBinaryData(cancellationToken),
                       serviceDirectory,
                       cancellationToken);

    public static new async ValueTask<Option<YamlOpenApiSpecificationFile>> TryParse(FileInfo? file, Func<FileInfo, ValueTask<BinaryData>> getFileContents, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        file is not null && file.Name == Name
        ? await ApiDirectory.TryParse(file.Directory, serviceDirectory)
                            .BindTask(async parent => from version in await OpenApiVersion.TryParse(await getFileContents(file), cancellationToken)
                                                      select new YamlOpenApiSpecificationFile
                                                      {
                                                          Parent = parent,
                                                          Version = version
                                                      })
            : Option<YamlOpenApiSpecificationFile>.None;
}

public sealed record JsonOpenApiSpecificationFile : OpenApiSpecificationFile
{
    public override OpenApiFormat Format { get; } = new OpenApiFormat.Json();
    public override ApiSpecification Specification => new ApiSpecification.OpenApi
    {
        Format = Format,
        Version = Version
    };

    public static string Name { get; } = "specification.json";

    protected override FileInfo Value => new(Path.Combine(Parent.ToDirectoryInfo().FullName, Name));

    public static JsonOpenApiSpecificationFile From(OpenApiVersion version, ApiName apiName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = ApiDirectory.From(apiName, serviceDirectory),
            Version = version
        };

    public static new async ValueTask<Option<JsonOpenApiSpecificationFile>> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await TryParse(file,
                       getFileContents: async file => await file.ReadAsBinaryData(cancellationToken),
                       serviceDirectory,
                       cancellationToken);

    public static new async ValueTask<Option<JsonOpenApiSpecificationFile>> TryParse(FileInfo? file, Func<FileInfo, ValueTask<BinaryData>> getFileContents, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        file is not null && file.Name == Name
        ? await ApiDirectory.TryParse(file.Directory, serviceDirectory)
                            .BindTask(async parent => from version in await OpenApiVersion.TryParse(await getFileContents(file), cancellationToken)
                                                      select new JsonOpenApiSpecificationFile
                                                      {
                                                          Parent = parent,
                                                          Version = version
                                                      })
            : Option<JsonOpenApiSpecificationFile>.None;
}