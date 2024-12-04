using codegen.resources;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace common.codegen;

[Generator]
public class CommonGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context) =>
        context.RegisterSourceOutput(GetProvider(context), GenerateContent);

    private static IncrementalValuesProvider<OutputSource> GetProvider(IncrementalGeneratorInitializationContext context) =>
        context.SyntaxProvider
               .CreateSyntaxProvider(IsInScope, GetModuleParameter)
               .Collect()
               .SelectMany((moduleParameters, _) => GetOutputSources(ApimResources.All, moduleParameters));

    private static bool IsInScope(SyntaxNode syntaxNode, CancellationToken _) =>
        syntaxNode switch
        {
            ParameterSyntax
            {
                Parent: ParameterListSyntax
                {
                    Parent: MethodDeclarationSyntax
                    {
                        Parent: ClassDeclarationSyntax classDeclarationSyntax
                    }
                }
            } => classDeclarationSyntax.Modifiers.Any(SyntaxKind.StaticKeyword)
                 && (classDeclarationSyntax.Modifiers.Any(SyntaxKind.PublicKeyword)
                     || classDeclarationSyntax.Modifiers.Any(SyntaxKind.InternalKeyword))
                 && classDeclarationSyntax.Identifier.ToString().EndsWith("Module", StringComparison.OrdinalIgnoreCase),
            _ => false
        };

    private static ModuleParameter GetModuleParameter(GeneratorSyntaxContext context, CancellationToken _) =>
        context.Node switch
        {
            ParameterSyntax
            {
                Parent: ParameterListSyntax
                {
                    Parent: MethodDeclarationSyntax
                    {
                        Parent: ClassDeclarationSyntax classDeclarationSyntax
                    } methodDeclarationSyntax
                }
            } parameterSyntax => new ModuleParameter
            {
                ClassName = classDeclarationSyntax.Identifier.ToString(),
                MethodName = methodDeclarationSyntax.Identifier.ToString(),
                ParameterName = parameterSyntax.Identifier.ToString()
            },
            _ => throw new InvalidOperationException($"Node type {context.Node.GetType()} is not supported.")
        };

    private static IEnumerable<OutputSource> GetOutputSources(IEnumerable<IResource> resources, IEnumerable<ModuleParameter> moduleParameters)
    {
        var parameterDictionary = GetClassModuleParameterDictionary(moduleParameters);

        return resources.Select(resource => new OutputSource
        {
            Resource = resource,
            ModuleMethodParameters = parameterDictionary.TryGetValue(resource.GetModuleClassName(), out var methodParameters)
                                     ? methodParameters
                                     : FrozenDictionary<string, FrozenSet<string>>.Empty
        });
    }

    private static FrozenDictionary<string, FrozenDictionary<string, FrozenSet<string>>> GetClassModuleParameterDictionary(IEnumerable<ModuleParameter> moduleParameters) =>
        moduleParameters.GroupBy(node => node.ClassName)
                        .ToFrozenDictionary(group => group.Key,
                                            group => group.GroupBy(node => node.MethodName)
                                                        .ToFrozenDictionary(methodGroup => methodGroup.Key,
                                                                            methodGroup => methodGroup.Select(node => node.ParameterName)
                                                                                                        .ToFrozenSet()));

    private sealed record ModuleParameter
    {
        public required string ClassName { get; init; }
        public required string MethodName { get; init; }
        public required string ParameterName { get; init; }
    }

    private sealed record OutputSource
    {
        public required IResource Resource { get; init; }
        public required FrozenDictionary<string, FrozenSet<string>> ModuleMethodParameters { get; init; }
    }

    private static void GenerateContent(SourceProductionContext context, OutputSource source) =>
        context.AddSource($"{source.Resource.GetType().Name}.common.g.cs",
                          GenerateContent(source));

    private static string GenerateContent(OutputSource source) =>
$$"""
using Azure.Core.Pipeline;
using Flurl;
using LanguageExt;
using System.Collections.Generic;
using System;
using System.Collections.Immutable;
using System.IO;
using System.Text.Json.Serialization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace common;

#nullable enable

{{GenerateResourceName(source)}}

{{GenerateResourceCollectionUri(source)}}

{{GenerateResourceUri(source)}}

{{GenerateResourceCollectionDirectory(source)}}

{{GenerateResourceDirectory(source)}}

{{GenerateResourceInformationFile(source)}}

{{GenerateResourcePolicyFile(source)}}

{{GenerateResourceDto(source)}}

{{GenerateModule(source)}}
""".RemoveExtraneousLinesFromCode();

    private static string GenerateResourceName(OutputSource source) =>
            source switch
            {
                { Resource: IResourceWithName resource } =>
$$"""
public sealed record {{resource.NameType}} : ResourceName
{
    private {{resource.NameType}}(string value) : base(value) { }

    public static Fin<{{resource.NameType}}> From(string? value) =>
        string.IsNullOrWhiteSpace(value)
        ? Fin<{{resource.NameType}}>.Fail($"{typeof({{resource.NameType}})} cannot be null or whitespace.")
        : new {{resource.NameType}}(value);
}
""",
                _ => string.Empty
            };

    private static string GenerateResourceCollectionUri(OutputSource source)
    {
        switch (source)
        {
            case { Resource: var resource }:
                var parentType = resource.TryGetParent()
                                        ?.UriType
                                 ?? "ServiceUri";

                var parentNameTypeWithParametersHierarchy =
                    resource.GetNameTypeWithParameterHierarchy()
                            .Skip(1)
                            .Concat(["ServiceUri serviceUri"])
                            .CommaSeparate();

                var parentNameTypeParametersHierarchy =
                    resource.GetNameTypeParameterHierarchy()
                            .Skip(1)
                            .Concat(["serviceUri"])
                            .CommaSeparate();

                var fromParameters = parentNameTypeParametersHierarchy == "serviceUri"
                                        ? "serviceUri"
                                        : $"{parentType}.From({parentNameTypeParametersHierarchy})";

                return $$"""
public sealed record {{resource.CollectionUriType}} : ResourceUri
{
    private {{resource.CollectionUriType}}({{parentType}} parent) : base(parent.ToUri().AppendPathSegment(PathSegment).ToUri()) =>
        Parent = parent;

    private static string PathSegment { get; } = "{{resource.CollectionUriPath}}";

    public {{parentType}} Parent { get; }

    public static {{resource.CollectionUriType}} From({{parentNameTypeWithParametersHierarchy}}) =>
        new({{fromParameters}});
}
""";
            default:
                return string.Empty;
        }
    }

    private static string GenerateResourceUri(OutputSource source)
    {
        switch (source)
        {
            case { Resource: var resource }:
                var resourceNameType = resource.GetNameTypeHierarchy().First();

                var resourceNameTypeParameter = resource.GetNameTypeParameterHierarchy()
                                                        .First();

                var resourceNameTypeWithParametersHierarchy =
                    resource.GetNameTypeWithParameterHierarchy()
                            .Concat(["ServiceUri serviceUri"])
                            .CommaSeparate();

                var parentNameTypeParametersHierarchy =
                    resource.GetNameTypeParameterHierarchy()
                            .Skip(1)
                            .Concat(["serviceUri"])
                            .CommaSeparate();

                return $$"""
public sealed record {{resource.UriType}} : ResourceUri
{
    private {{resource.UriType}}({{resourceNameType}} name, {{resource.CollectionUriType}} parent) : base(parent.ToUri().AppendPathSegment(name.ToString()).ToUri())
    {
        Parent = parent;
        Name = name;
    }

    public {{resource.CollectionUriType}} Parent { get; }

    public {{resourceNameType}} Name { get; }

    public static {{resource.UriType}} From({{resourceNameTypeWithParametersHierarchy}}) =>
        new({{resourceNameTypeParameter}}, {{resource.CollectionUriType}}.From({{parentNameTypeParametersHierarchy}}));

    internal static {{resource.UriType}} From({{resourceNameType}} name, {{resource.CollectionUriType}} parent) =>
        new(name, parent);
}
""";
            default:
                return string.Empty;
        }
    }

    private static string GenerateResourceCollectionDirectory(OutputSource source)
    {
        switch (source)
        {
            case { Resource: IResourceWithDirectory resource }:
                var parentDirectoryType = resource.TryGetParent() switch
                {
                    IResourceWithDirectory parent => parent.DirectoryType,
                    _ => "ServiceDirectory"
                };

                var parentNameTypeWithParametersHierarchy =
                    resource.GetNameTypeWithParameterHierarchy()
                            .Skip(1)
                            .Concat(["ServiceDirectory serviceDirectory"])
                            .CommaSeparate();

                var parentNameTypeParametersHierarchy =
                    resource.GetNameTypeParameterHierarchy()
                            .Skip(1)
                            .Concat(["serviceDirectory"])
                            .CommaSeparate();

                var fromParameters = parentNameTypeParametersHierarchy == "serviceDirectory"
                                        ? "serviceDirectory"
                                        : $"{parentDirectoryType}.From({parentNameTypeParametersHierarchy})";

                return $$"""
public sealed record {{resource.CollectionDirectoryType}} : ResourceDirectory
{
    private const string Name = "{{resource.CollectionDirectoryName}}";

    private {{resource.CollectionDirectoryType}}({{parentDirectoryType}} parent) : base(parent.ToDirectoryInfo().GetChildDirectory(Name).FullName) =>
        Parent = parent;

    public {{parentDirectoryType}} Parent { get; }

    public static {{resource.CollectionDirectoryType}} From({{parentNameTypeWithParametersHierarchy}}) =>
        new({{fromParameters}});

                {{parentDirectoryType switch
                {
                    "ServiceDirectory" => string.Empty,
                    _ =>
$$"""

    internal static {{resource.CollectionDirectoryType}} From({{parentDirectoryType}} parent) =>
        new(parent);
"""
                }}}

    public static Option<{{resource.CollectionDirectoryType}}> Parse(DirectoryInfo? directory, ServiceDirectory serviceDirectory) =>
        directory switch
        {
            { Name: Name } =>
{{(parentNameTypeParametersHierarchy == "serviceDirectory"
    ?
"""
                from parent in Option<ServiceDirectory>.Some(serviceDirectory)
                where directory.Parent?.FullName == parent.ToDirectoryInfo().FullName
"""
    :
$"""
                from parent in {parentDirectoryType}.Parse(directory?.Parent, serviceDirectory)
""")}}
                select new {{resource.CollectionDirectoryType}}(parent),
            _ => Option<{{resource.CollectionDirectoryType}}>.None
        };
}
""";
            default:
                return string.Empty;
        }
    }

    private static string GenerateResourceDirectory(OutputSource source)
    {
        switch (source)
        {
            case { Resource: IResourceWithDirectory resource }:
                var resourceNameType = resource.GetNameTypeHierarchy().First();

                var resourceNameTypeParameter = resource.GetNameTypeParameterHierarchy()
                                                        .First();

                var resourceNameTypeWithParametersHierarchy =
                    resource.GetNameTypeWithParameterHierarchy()
                            .Concat(["ServiceDirectory serviceDirectory"])
                            .CommaSeparate();

                var parentNameTypeParametersHierarchy =
                    resource.GetNameTypeParameterHierarchy()
                            .Skip(1)
                            .Concat(["serviceDirectory"])
                            .CommaSeparate();
                return
$$"""
public sealed record {{resource.DirectoryType}} : ResourceDirectory
{
    private {{resource.DirectoryType}}({{resourceNameType}} name, {{resource.CollectionDirectoryType}} parent) : base(parent.ToDirectoryInfo().GetChildDirectory(name.ToString()).FullName)
    {
        Parent = parent;
        Name = name;
    }

    public {{resource.CollectionDirectoryType}} Parent { get; }

    public {{resourceNameType}} Name { get; }

    public static {{resource.DirectoryType}} From({{resourceNameTypeWithParametersHierarchy}}) =>
        new({{resourceNameTypeParameter}}, {{resource.CollectionDirectoryType}}.From({{parentNameTypeParametersHierarchy}}));

    internal static {{resource.DirectoryType}} From({{resourceNameType}} name, {{resource.CollectionDirectoryType}} parent) =>
        new(name, parent);

    public static Option<{{resource.DirectoryType}}> Parse(DirectoryInfo? directory, ServiceDirectory serviceDirectory) =>
        from parent in {{resource.CollectionDirectoryType}}.Parse(directory?.Parent, serviceDirectory)
        from name in {{resourceNameType}}.From(directory?.Name).ToOption()
        select new {{resource.DirectoryType}}(name, parent);
}
""";
            default:
                return string.Empty;
        }
    }

    private static string GenerateResourceInformationFile(OutputSource source)
    {
        switch (source)
        {
            case { Resource: IResourceWithInformationFile resource }:
                var resourceNameTypeParametersHierarchy =
                    resource.GetNameTypeParameterHierarchy()
                            .Concat(["serviceDirectory"])
                            .CommaSeparate();

                var resourceNameTypeWithParametersHierarchy =
                    resource.GetNameTypeWithParameterHierarchy()
                            .Concat(["ServiceDirectory serviceDirectory"])
                            .CommaSeparate();

                return
$$"""
public sealed record {{resource.InformationFileType}} : ResourceFile
{
    private const string Name = "{{resource.InformationFileName}}";

    private {{resource.InformationFileType}}({{resource.DirectoryType}} parent) : base(parent.ToDirectoryInfo().GetChildFile(Name).FullName) =>
        Parent = parent;

    public {{resource.DirectoryType}} Parent { get; }

    public static {{resource.InformationFileType}} From({{resourceNameTypeWithParametersHierarchy}}) =>
        new({{resource.DirectoryType}}.From({{resourceNameTypeParametersHierarchy}}));

    internal static {{resource.InformationFileType}} From({{resource.DirectoryType}} parent) =>
        new(parent);

    public static Option<{{resource.InformationFileType}}> Parse(FileInfo? file, ServiceDirectory serviceDirectory) =>
        file switch
        {
            { Name: Name } => from parent in {{resource.DirectoryType}}.Parse(file?.Directory, serviceDirectory)
                                select new {{resource.InformationFileType}}(parent),
            _ => Option<{{resource.InformationFileType}}>.None
        };
}
""";
            default:
                return string.Empty;
        };
    }

    private static string GenerateResourcePolicyFile(OutputSource source)
    {
        switch (source)
        {
            case { Resource: IPolicyResource resource }:
                var parentDirectoryType = resource.TryGetParent() switch
                {
                    IResourceWithDirectory parent => parent.DirectoryType,
                    _ => "ServiceDirectory"
                };

                var resourceNameTypeParameter = resource.GetNameTypeParameterHierarchy()
                                                        .First();

                var resourceNameTypeWithParametersHierarchy =
                    resource.GetNameTypeWithParameterHierarchy()
                            .Concat(["ServiceDirectory serviceDirectory"])
                            .CommaSeparate();

                var parentNameTypeParametersHierarchy =
                    resource.GetNameTypeParameterHierarchy()
                            .Skip(1)
                            .Concat(["serviceDirectory"])
                            .CommaSeparate();

                var fromParameters = parentNameTypeParametersHierarchy == "serviceDirectory"
                                        ? "serviceDirectory"
                                        : $"{parentDirectoryType}.From({parentNameTypeParametersHierarchy})";

                return
$$""""
public sealed record {{resource.PolicyFileType}} : ResourceFile
{
    private {{resource.PolicyFileType}}({{resource.NameType}} name, {{parentDirectoryType}} parent) : base(parent.ToDirectoryInfo().GetChildFile($"{name}.xml").FullName)
    {
        Parent = parent;
        Name = name;
    }

    public {{parentDirectoryType}} Parent { get; }

    public {{resource.NameType}} Name { get; }

    public static {{resource.PolicyFileType}} From({{resourceNameTypeWithParametersHierarchy}}) =>
        new({{resourceNameTypeParameter}}, {{fromParameters}});

    public static Option<{{resource.PolicyFileType}}> Parse(FileInfo? file, ServiceDirectory serviceDirectory) =>
        file switch
        {
            { Name: string fileName } when fileName.EndsWith(".xml", StringComparison.Ordinal) =>
{{(parentDirectoryType == "serviceDirectory"
    ?
"""
                from parent in Option<ServiceDirectory>.Some(serviceDirectory)
                where file?.Directory?.FullName == parent.ToDirectoryInfo().FullName
"""
    :
$"""
                from parent in {parentDirectoryType}.Parse(file?.Directory, serviceDirectory)
""")}}
                let name = {{resource.NameType}}.From(Path.GetFileNameWithoutExtension(fileName)).ThrowIfFail()
                select new {{resource.PolicyFileType}}(name, parent),
            _ => Option<{{resource.PolicyFileType}}>.None
        };
}
"""";
            default:
                return string.Empty;
        };
    }

    private static string GenerateResourceDto(OutputSource source) =>
        source switch
        {
            { Resource: IResourceWithDto resourceWithDto } =>
$$"""
public sealed record {{resourceWithDto.DtoType}}
{
{{resourceWithDto.DtoCode}}
}
""",
            _ => string.Empty
        };

    private static string GenerateModule(OutputSource source) =>
$$"""
public static partial class {{source.Resource.GetModuleClassName()}}
{
{{GenerateModuleGetOptionalDto(source)}}

{{GenerateModuleGetDto(source)}}

{{GenerateModuleListNames(source)}}

{{GenerateModuleList(source)}}

{{GenerateModulePutDto(source)}}

{{GenerateModuleDelete(source)}}

{{GenerateModuleDeleteAll(source)}}

{{GenerateModuleListDirectories(source)}}

{{GenerateModuleListInformationFiles(source)}}

{{GenerateModuleWriteDto(source)}}

{{GenerateModuleReadDto(source)}}
}
""";

    private static bool MethodExists(string methodName, IEnumerable<string> parameterNames, IDictionary<string, FrozenSet<string>> methodParameters) =>
        methodParameters.TryGetValue(methodName, out var parameters)
        && parameterNames.All(parameters.Contains);

    private static string GenerateModuleGetOptionalDto(OutputSource source) =>
        source switch
        {
            _ when MethodExists(methodName: "GetOptionalDto",
                                parameterNames: ["uri", "pipeline", "cancellationToken"],
                                source.ModuleMethodParameters) => string.Empty,
            { Resource: IResourceWithDto resourceWithDto } =>
$$"""
    public static async ValueTask<Option<{{resourceWithDto.DtoType}}>> GetOptionalDto(this {{resourceWithDto.UriType}} uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var contentOption = await pipeline.GetContentOption(uri.ToUri(), cancellationToken);

        return contentOption.Map(content => JsonNodeModule.Deserialize<{{resourceWithDto.DtoType}}>(content))
                            .Map(result => result.ThrowIfFail());
    }
""",
            _ => string.Empty
        };

    private static string GenerateModuleGetDto(OutputSource source) =>
        source switch
        {
            _ when MethodExists(methodName: "GetDto",
                                parameterNames: ["uri", "pipeline", "cancellationToken"],
                                source.ModuleMethodParameters) => string.Empty,
            { Resource: IResourceWithDto resourceWithDto } =>
$$"""
    public static async ValueTask<{{resourceWithDto.DtoType}}> GetDto(this {{resourceWithDto.UriType}} uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = await pipeline.GetContent(uri.ToUri(), cancellationToken);

        return JsonNodeModule.Deserialize<{{resourceWithDto.DtoType}}>(content)
                             .ThrowIfFail();
    }
""",
            _ => string.Empty
        };

    private static string GenerateModuleListNames(OutputSource source) =>
        source switch
        {
            _ when MethodExists(methodName: "ListNames",
                                parameterNames: ["uri", "pipeline", "cancellationToken"],
                                source.ModuleMethodParameters) => string.Empty,
            { Resource: IResource resource } =>
$$"""
    public static IAsyncEnumerable<{{resource.GetBestNameType()}}> ListNames(this {{resource.CollectionUriType}} uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => from nameString in jsonObject.GetStringProperty("name")
                                                                   .ToFin()
                                      from name in {{resource.GetBestNameType()}}.From(nameString)
                                      select name)
                .Select(f => f.ThrowIfFail())
                .Catch(AzureModule.GetMethodNotAllowedInPricingTierHandler<{{resource.GetBestNameType()}}>());
"""
        };

    private static string GenerateModuleList(OutputSource source) =>
        source switch
        {
            _ when MethodExists(methodName: "List",
                                parameterNames: ["uri", "pipeline", "cancellationToken"],
                                source.ModuleMethodParameters) => string.Empty,
            { Resource: IResourceWithDto resourceWithDto and IResourceWithName resourceWithName } =>
$$"""
    public static IAsyncEnumerable<({{resourceWithName.NameType}} Name, {{resourceWithDto.DtoType}} Dto)> List(this {{resourceWithDto.CollectionUriType}} uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        uri.ListNames(pipeline, cancellationToken)
           .SelectAwait(async name =>
           {
                var resourceUri = {{resourceWithDto.UriType}}.From(name, uri);
                var dto = await resourceUri.GetDto(pipeline, cancellationToken);
                return (name, dto);
           });
""",
            _ => string.Empty
        };

    private static string GenerateModulePutDto(OutputSource source) =>
        source switch
        {
            _ when MethodExists(methodName: "PutDto",
                                parameterNames: ["uri", "dto", "pipeline", "cancellationToken"],
                                source.ModuleMethodParameters) => string.Empty,
            { Resource: IResourceWithDto resourceWithDto } =>
$$"""
    public static async ValueTask PutDto(this {{resourceWithDto.UriType}} uri, {{resourceWithDto.DtoType}} dto, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto);

        await pipeline.PutContent(uri.ToUri(), content, cancellationToken);
    }
""",
            _ => string.Empty
        };

    private static string GenerateModuleDelete(OutputSource source) =>
        source switch
        {
            _ when MethodExists(methodName: "Delete",
                                parameterNames: ["uri", "pipeline", "cancellationToken"],
                                source.ModuleMethodParameters) => string.Empty,
            { Resource: var resource } =>
$$"""
    public static async ValueTask Delete(this {{resource.UriType}} uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await pipeline.DeleteResource(uri.ToUri(), waitForCompletion: true, cancellationToken);
"""
        };

    private static string GenerateModuleDeleteAll(OutputSource source) =>
        source switch
        {
            _ when MethodExists(methodName: "DeleteAll",
                                parameterNames: ["uri", "pipeline", "cancellationToken"],
                                source.ModuleMethodParameters) => string.Empty,
            { Resource: var resource } =>
$$"""
    public static async ValueTask DeleteAll(this {{resource.CollectionUriType}} uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await uri.ListNames(pipeline, cancellationToken)
                 .IterParallel(async name =>
                 {
                     var resourceUri = {{resource.UriType}}.From(name, uri);
                     await resourceUri.Delete(pipeline, cancellationToken);
                 },
                 cancellationToken);
"""
        };

    private static string GenerateModuleListDirectories(OutputSource source) =>
        source switch
        {
            _ when MethodExists(methodName: "ListDirectories",
                                parameterNames: ["serviceDirectory"],
                                source.ModuleMethodParameters) => string.Empty,
            { Resource: IResourceWithDirectory resourceWithDirectory and IResourceWithName resourceWithName }
                when resourceWithDirectory.TryGetParent() is IResourceWithDirectory parentResource =>
$$"""
    public static IEnumerable<{{resourceWithDirectory.DirectoryType}}> ListDirectories(ServiceDirectory serviceDirectory) =>
        from parentDirectory in {{parentResource.GetModuleClassName()}}.ListDirectories(serviceDirectory)
        let collectionDirectory = {{resourceWithDirectory.CollectionDirectoryType}}.From(parentDirectory)
        from resourceDirectory in collectionDirectory.ToDirectoryInfo().ListDirectories("*")
        let name = {{resourceWithName.NameType}}.From(resourceDirectory.Name).ThrowIfFail()
        select {{resourceWithDirectory.DirectoryType}}.From(name, collectionDirectory);
""",
            { Resource: IResourceWithDirectory resourceWithDirectory and IResourceWithName resourceWithName } =>
$$"""
    public static IEnumerable<{{resourceWithDirectory.DirectoryType}}> ListDirectories(ServiceDirectory serviceDirectory) =>
        from collectionDirectory in new[] { {{resourceWithDirectory.CollectionDirectoryType}}.From(serviceDirectory) }
        from resourceDirectory in collectionDirectory.ToDirectoryInfo().ListDirectories("*")
        let name = {{resourceWithName.NameType}}.From(resourceDirectory.Name).ThrowIfFail()
        select {{resourceWithDirectory.DirectoryType}}.From(name, collectionDirectory);
""",
            _ => string.Empty
        };

    private static string GenerateModuleListInformationFiles(OutputSource source) =>
        source switch
        {
            _ when MethodExists(methodName: "ListInformationFiles",
                                parameterNames: ["serviceDirectory"],
                                source.ModuleMethodParameters) => string.Empty,
            { Resource: IResourceWithInformationFile resourceWithInformationFile } =>
$$"""
    public static IEnumerable<{{resourceWithInformationFile.InformationFileType}}> ListInformationFiles(ServiceDirectory serviceDirectory) =>
        ListDirectories(serviceDirectory)
            .Select({{resourceWithInformationFile.InformationFileType}}.From)
            .Where(informationFile => informationFile.ToFileInfo().Exists());
""",
            _ => string.Empty
        };

    private static string GenerateModuleWriteDto(OutputSource source) =>
        source switch
        {
            _ when MethodExists(methodName: "WriteDto",
                                parameterNames: ["file", "dto", "cancellationToken"],
                                source.ModuleMethodParameters) => string.Empty,
            { Resource: IResourceWithInformationFile resourceWithInformationFile } =>
$$"""
    public static async ValueTask WriteDto(this {{resourceWithInformationFile.InformationFileType}} file, {{resourceWithInformationFile.DtoType}} dto, CancellationToken cancellationToken) =>
        await file.ToFileInfo().OverwriteWithJson(dto, cancellationToken);
""",
            _ => string.Empty
        };

    private static string GenerateModuleReadDto(OutputSource source) =>
        source switch
        {
            _ when MethodExists(methodName: "ReadDto",
                                parameterNames: ["file", "cancellationToken"],
                                source.ModuleMethodParameters) => string.Empty,
            { Resource: IResourceWithInformationFile resourceWithInformationFile } =>
$$"""
    public static async ValueTask<{{resourceWithInformationFile.DtoType}}> ReadDto(this {{resourceWithInformationFile.InformationFileType}} file, CancellationToken cancellationToken)
    {
        var content = await file.ToFileInfo().ReadAsBinaryData(cancellationToken);

        return JsonNodeModule.Deserialize<{{resourceWithInformationFile.DtoType}}>(content)
                             .ThrowIfFail();
    }
""",
            _ => string.Empty
        };
}