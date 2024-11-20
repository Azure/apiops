using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;

namespace common.codegen;

[Generator]
public class CommonGenerator : IIncrementalGenerator
{
    private readonly ImmutableArray<IResource> resources = [
        new NamedValue()
        ];

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(context =>
        {
            foreach (var resource in resources)
            {
                var fileName = $"{resource.GetType().Name}.common.g.cs";

                context.AddSource(fileName, GenerateResourceContent(resource));
            }
        });

        var provider = context.SyntaxProvider
                              .CreateSyntaxProvider(IsModuleParameter, GetClassMethodParameter)
                              .Collect()
                              .SelectMany((classMethodParameters, token) =>
                              {
                                  var dictionary = GetClassMethodParameterDictionary(classMethodParameters);

                                  return resources.Select(resource => dictionary.TryGetValue(new ClassName(resource.ModuleType),
                                                                                             out var methodParameters)
                                                                      ? (resource, methodParameters)
                                                                      : (resource, FrozenDictionary<MethodName, FrozenSet<ParameterName>>.Empty));
                              });

        context.RegisterSourceOutput(provider,
                                     (context, x) => RegisterModuleOutput(context, x.resource, x.Item2));
    }

    private static bool IsModuleParameter(SyntaxNode syntaxNode, CancellationToken _) =>
        syntaxNode is ParameterSyntax parameterSyntax
        && parameterSyntax.Parent is ParameterListSyntax parameterListSyntax
        && parameterListSyntax.Parent is MethodDeclarationSyntax methodDeclarationSyntax
        && methodDeclarationSyntax.Modifiers.Any(SyntaxKind.PublicKeyword)
        && methodDeclarationSyntax.Modifiers.Any(SyntaxKind.StaticKeyword)
        && methodDeclarationSyntax.Parent is ClassDeclarationSyntax classDeclarationSyntax
        && classDeclarationSyntax.Identifier.ToString().EndsWith("Module")
        && classDeclarationSyntax.Modifiers.Any(SyntaxKind.PublicKeyword)
        && classDeclarationSyntax.Modifiers.Any(SyntaxKind.StaticKeyword);

    private static (ClassName ClassName, MethodName MethodName, ParameterName ParameterName) GetClassMethodParameter(GeneratorSyntaxContext context, CancellationToken _)
    {
        var parameterSyntax = (ParameterSyntax)context.Node;
        var parameterListSyntax = (ParameterListSyntax)parameterSyntax.Parent!;
        var methodSyntax = (MethodDeclarationSyntax)parameterListSyntax.Parent!;
        var classSyntax = (ClassDeclarationSyntax)methodSyntax.Parent!;

        return (new ClassName(classSyntax.Identifier.ToString()),
                new MethodName(methodSyntax.Identifier.ToString()),
                new ParameterName(parameterSyntax.Identifier.ToString()));
    }

    private static FrozenDictionary<ClassName, FrozenDictionary<MethodName, FrozenSet<ParameterName>>> GetClassMethodParameterDictionary(IEnumerable<(ClassName ClassName, MethodName MethodName, ParameterName ParameterName)> classMethodParameters) =>
        classMethodParameters.GroupBy(x => x.ClassName)
                             .ToFrozenDictionary(group => group.Key,
                                                    group => group.GroupBy(x => x.MethodName)
                                                                  .ToFrozenDictionary(group => group.Key,
                                                                                         group => group.Select(x => x.ParameterName)
                                                                                                       .ToFrozenSet()));

    private sealed record ClassName(string Value);
    private sealed record MethodName(string Value);
    private sealed record ParameterName(string Value);

    private static string GenerateResourceContent(IResource resource) =>
$$"""
{{GenerateUsings(resource)}}

namespace common;

#nullable enable

{{GenerateResourceName(resource)}}

{{GenerateResourceCollectionUri(resource)}}

{{GenerateResourceUri(resource)}}

{{GenerateResourceCollectionDirectory(resource)}}

{{GenerateResourceDirectory(resource)}}

{{GenerateResourceInformationFile(resource)}}

{{GenerateResourceDto(resource)}}
""";

    private static string GenerateUsings(IResource resource)
    {
        HashSet<string> usings = [
            "Flurl",
            "LanguageExt",
            "System.IO"
        ];

        if (resource is IResourceWithDto resourceWithDto)
        {
            usings.Add("System.Text.Json.Serialization");
            if (resourceWithDto.DtoCode.Contains("Immutable"))
            {
                usings.Add("System.Collections.Immutable");
            }
        }

        return usings.Aggregate(new StringBuilder(), (sb, u) => sb.AppendLine($"using {u};"))
                     .ToString();
    }

    private static string GenerateResourceName(IResource resource) =>
$$"""
public sealed record {{resource.NameType}} : ResourceName
{
    private {{resource.NameType}}(string value) : base(value) { }

    public static Fin<{{resource.NameType}}> From(string? value) =>
        string.IsNullOrWhiteSpace(value)
        ? Fin<{{resource.NameType}}>.Fail($"{typeof({{resource.NameType}})} cannot be null or whitespace.")
        : new {{resource.NameType}}(value);
}
""";

    private static string GenerateResourceCollectionUri(IResource resource) =>
        resource switch
        {
            IChildResource => string.Empty,
            _ =>
$$"""
public sealed record {{resource.CollectionUriType}} : ResourceUri
{
    private {{resource.CollectionUriType}}(ServiceUri parent) : base(parent.ToUri().AppendPathSegment(PathSegment).ToUri()) =>
        Parent = parent;

    private static string PathSegment { get; } = "namedValues";

    public ServiceUri Parent { get; }

    public static {{resource.CollectionUriType}} From(ServiceUri serviceUri) =>
        new(serviceUri);
}
"""
        };

    private static string GenerateResourceUri(IResource resource) =>
$$"""
public sealed record {{resource.UriType}} : ResourceUri
{
    private {{resource.UriType}}({{resource.NameType}} name, {{resource.CollectionUriType}} parent) : base(parent.ToUri().AppendPathSegment(name.ToString()).ToUri())
    {
        Parent = parent;
        Name = name;
    }

    public {{resource.CollectionUriType}} Parent { get; }

    public {{resource.NameType}} Name { get; }

    public static {{resource.UriType}} From({{resource.NameType}} name, ServiceUri serviceUri) =>
        new(name, {{resource.CollectionUriType}}.From(serviceUri));

    internal static {{resource.UriType}} From({{resource.NameType}} name, {{resource.CollectionUriType}} parent) =>
        new(name, parent);
}
""";

    private static string GenerateResourceCollectionDirectory(IResource resource) =>
        resource switch
        {
            IResourceWithDirectory resourceWithDirectory and not IChildResource =>
$$"""
public sealed record {{resourceWithDirectory.CollectionDirectoryType}} : ResourceDirectory
{
    private const string Name = "{{resourceWithDirectory.CollectionDirectoryName}}";

    private {{resourceWithDirectory.CollectionDirectoryType}}(ServiceDirectory parent) : base(parent.ToDirectoryInfo().GetChildDirectory(Name).FullName) =>
        Parent = parent;

    public ServiceDirectory Parent { get; }

    public static {{resourceWithDirectory.CollectionDirectoryType}} From(ServiceDirectory serviceDirectory) =>
        new(serviceDirectory);

    public static Option<{{resourceWithDirectory.CollectionDirectoryType}}> Parse(DirectoryInfo? directory, ServiceDirectory serviceDirectory) =>
        directory switch
        {
            { Name: Name } when directory.Parent?.FullName == serviceDirectory.ToDirectoryInfo().FullName => new {{resourceWithDirectory.CollectionDirectoryType}}(serviceDirectory),
            _ => Option<{{resourceWithDirectory.CollectionDirectoryType}}>.None
        };
}
""",
            _ => string.Empty
        };

    private static string GenerateResourceDirectory(IResource resource) =>
        resource switch
        {
            IResourceWithDirectory resourceWithDirectory =>
$$"""
public sealed record {{resourceWithDirectory.DirectoryType}} : ResourceDirectory
{
    private {{resourceWithDirectory.DirectoryType}}({{resource.NameType}} name, {{resourceWithDirectory.CollectionDirectoryType}} parent) : base(parent.ToDirectoryInfo().GetChildDirectory(name.ToString()).FullName)
    {
        Parent = parent;
        Name = name;
    }

    public {{resourceWithDirectory.CollectionDirectoryType}} Parent { get; }

    public {{resource.NameType}} Name { get; }

    public static {{resourceWithDirectory.DirectoryType}} From({{resource.NameType}} name, ServiceDirectory serviceDirectory) =>
        new(name, {{resourceWithDirectory.CollectionDirectoryType}}.From(serviceDirectory));

    internal static {{resourceWithDirectory.DirectoryType}} From({{resource.NameType}} name, {{resourceWithDirectory.CollectionDirectoryType}} parent) =>
        new(name, parent);

    public static Option<{{resourceWithDirectory.DirectoryType}}> Parse(DirectoryInfo? directory, ServiceDirectory serviceDirectory) =>
        from parent in {{resourceWithDirectory.CollectionDirectoryType}}.Parse(directory?.Parent, serviceDirectory)
        from name in {{resource.NameType}}.From(directory?.Name).ToOption()
        select new {{resourceWithDirectory.DirectoryType}}(name, parent);
}
""",
            _ => string.Empty
        };

    private static string GenerateResourceInformationFile(IResource resource) =>
        resource switch
        {
            IResourceWithInformationFile resourceWithInformationFile =>
$$"""
public sealed record {{resourceWithInformationFile.InformationFileType}} : ResourceFile
{
    private const string Name = "{{resourceWithInformationFile.InformationFileName}}";

    private {{resourceWithInformationFile.InformationFileType}}({{resourceWithInformationFile.DirectoryType}} parent) : base(parent.ToDirectoryInfo().GetChildFile(Name).FullName) =>
        Parent = parent;

    public {{resourceWithInformationFile.DirectoryType}} Parent { get; }

    public static {{resourceWithInformationFile.InformationFileType}} From({{resource.NameType}} name, ServiceDirectory serviceDirectory) =>
        new({{resourceWithInformationFile.DirectoryType}}.From(name, serviceDirectory));

    public static Option<{{resourceWithInformationFile.InformationFileType}}> Parse(FileInfo? file, ServiceDirectory serviceDirectory) =>
        file switch
        {
            { Name: Name } => from parent in {{resourceWithInformationFile.DirectoryType}}.Parse(file?.Directory, serviceDirectory)
                              select new {{resourceWithInformationFile.InformationFileType}}(parent),
            _ => Option<{{resourceWithInformationFile.InformationFileType}}>.None
        };
}
""",
            _ => string.Empty
        };

    private static string GenerateResourceDto(IResource resource) =>
        resource switch
        {
            IResourceWithDto resourceWithDto =>
$$"""
public sealed record {{resourceWithDto.DtoType}}
{
{{resourceWithDto.DtoCode}}
}
""",
            _ => string.Empty
        };

    private static void RegisterModuleOutput(SourceProductionContext context, IResource resource, IDictionary<MethodName, FrozenSet<ParameterName>> methodParameters)
    {
        context.AddSource($"{resource.GetType().Name}.common.Module.g.cs",
                          GenerateModuleContent(resource, methodParameters));
    }

    private static string GenerateModuleContent(IResource resource, IDictionary<MethodName, FrozenSet<ParameterName>> methodParameters) =>
$$"""
using Azure.Core.Pipeline;
using LanguageExt;
using System.Collections.Generic;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace common;

#nullable enable

public static partial class {{resource.ModuleType}}
{
{{GenerateModuleGetOptionalDto(resource, methodParameters)}}

{{GenerateModuleGetDto(resource, methodParameters)}}

{{GenerateModuleListNames(resource, methodParameters)}}

{{GenerateModuleList(resource, methodParameters)}}

{{GenerateModulePutDto(resource, methodParameters)}}

{{GenerateModuleDelete(resource, methodParameters)}}

{{GenerateModuleDeleteAll(resource, methodParameters)}}

{{GenerateModuleListDirectories(resource, methodParameters)}}

{{GenerateModuleListInformationFiles(resource, methodParameters)}}

{{GenerateModuleWriteDto(resource, methodParameters)}}

{{GenerateModuleReadDto(resource, methodParameters)}}
}
""";

    private static bool MethodExists(string methodName, IEnumerable<string> parameterNames, IDictionary<MethodName, FrozenSet<ParameterName>> methodParameters) =>
        methodParameters.TryGetValue(new MethodName(methodName), out var parameters)
        && parameterNames.All(parameterName => parameters.Contains(new ParameterName(parameterName)));

    private static string GenerateModuleGetOptionalDto(IResource resource, IDictionary<MethodName, FrozenSet<ParameterName>> methodParameters) =>
        resource switch
        {
            _ when MethodExists(methodName: "GetOptionalDto",
                                parameterNames: ["uri", "pipeline", "cancellationToken"],
                                methodParameters) => string.Empty,
            IResourceWithDto resourceWithDto =>
$$"""
    public static async ValueTask<Option<{{resourceWithDto.DtoType}}>> GetOptionalDto(this {{resource.UriType}} uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var contentOption = await pipeline.GetContentOption(uri.ToUri(), cancellationToken);

        return contentOption.Map(content => JsonNodeModule.Deserialize<{{resourceWithDto.DtoType}}>(content))
                            .Map(result => result.ThrowIfFail());
    }
""",
            _ => string.Empty
        };

    private static string GenerateModuleGetDto(IResource resource, IDictionary<MethodName, FrozenSet<ParameterName>> methodParameters) =>
        resource switch
        {
            _ when MethodExists(methodName: "GetDto",
                                parameterNames: ["uri", "pipeline", "cancellationToken"],
                                methodParameters) => string.Empty,
            IResourceWithDto resourceWithDto =>
$$"""
    public static async ValueTask<{{resourceWithDto.DtoType}}> GetDto(this {{resource.UriType}} uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = await pipeline.GetContent(uri.ToUri(), cancellationToken);

        return JsonNodeModule.Deserialize<{{resourceWithDto.DtoType}}>(content)
                             .ThrowIfFail();
    }
""",
            _ => string.Empty
        };

    private static string GenerateModuleListNames(IResource resource, IDictionary<MethodName, FrozenSet<ParameterName>> methodParameters) =>
        resource switch
        {
            _ when MethodExists(methodName: "ListNames",
                                parameterNames: ["uri", "pipeline", "cancellationToken"],
                                methodParameters) => string.Empty,
            _ =>
$$"""
    public static IAsyncEnumerable<{{resource.NameType}}> ListNames(this {{resource.CollectionUriType}} uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => from nameString in jsonObject.GetStringProperty("name")
                                                                   .ToFin()
                                      from name in {{resource.NameType}}.From(nameString)
                                      select name)
                .Select(f => f.ThrowIfFail())
                .Catch(AzureModule.GetMethodNotAllowedInPricingTierHandler<{{resource.NameType}}>());
"""
        };

    private static string GenerateModuleList(IResource resource, IDictionary<MethodName, FrozenSet<ParameterName>> methodParameters) =>
        resource switch
        {
            _ when MethodExists(methodName: "List",
                                parameterNames: ["uri", "pipeline", "cancellationToken"],
                                methodParameters) => string.Empty,
            IResourceWithDto resourceWithDto =>
$$"""
    public static IAsyncEnumerable<({{resource.NameType}} Name, {{resourceWithDto.DtoType}} Dto)> List(this {{resource.CollectionUriType}} uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        uri.ListNames(pipeline, cancellationToken)
           .SelectAwait(async name =>
           {
               var resourceUri = {{resource.UriType}}.From(name, uri);
               var dto = await resourceUri.GetDto(pipeline, cancellationToken);
               return (name, dto);
           });
""",
            _ => string.Empty
        };

    private static string GenerateModulePutDto(IResource resource, IDictionary<MethodName, FrozenSet<ParameterName>> methodParameters) =>
        resource switch
        {
            _ when MethodExists(methodName: "PutDto",
                                parameterNames: ["uri", "dto", "pipeline", "cancellationToken"],
                                methodParameters) => string.Empty,
            IResourceWithDto resourceWithDto =>
$$"""
    public static async ValueTask PutDto(this {{resource.UriType}} uri, {{resourceWithDto.DtoType}} dto, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto);

        await pipeline.PutContent(uri.ToUri(), content, cancellationToken);
    }
""",
            _ => string.Empty
        };

    private static string GenerateModuleDelete(IResource resource, IDictionary<MethodName, FrozenSet<ParameterName>> methodParameters) =>
        resource switch
        {
            _ when MethodExists(methodName: "Delete",
                                parameterNames: ["uri", "pipeline", "cancellationToken"],
                                methodParameters) => string.Empty,
            _ =>
$$"""
    public static async ValueTask Delete(this {{resource.UriType}} uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await pipeline.DeleteResource(uri.ToUri(), waitForCompletion: true, cancellationToken);
"""
        };

    private static string GenerateModuleDeleteAll(IResource resource, IDictionary<MethodName, FrozenSet<ParameterName>> methodParameters) =>
        resource switch
        {
            _ when MethodExists(methodName: "DeleteAll",
                                parameterNames: ["uri", "pipeline", "cancellationToken"],
                                methodParameters) => string.Empty,
            _ =>
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

    private static string GenerateModuleListDirectories(IResource resource, IDictionary<MethodName, FrozenSet<ParameterName>> methodParameters) =>
        resource switch
        {
            _ when MethodExists(methodName: "ListDirectories",
                                parameterNames: ["serviceDirectory"],
                                methodParameters) => string.Empty,
            IResourceWithDirectory resourceWithDirectory =>
$$"""
    public static IEnumerable<{{resourceWithDirectory.DirectoryType}}> ListDirectories(ServiceDirectory serviceDirectory)
    {
        var collectionDirectory = {{resourceWithDirectory.CollectionDirectoryType}}.From(serviceDirectory);

        return collectionDirectory.ToDirectoryInfo()
                                  .ListDirectories("*")
                                  .Select(directoryInfo => {{resource.NameType}}.From(directoryInfo.Name).ThrowIfFail())
                                  .Select(name => {{resourceWithDirectory.DirectoryType}}.From(name, collectionDirectory));
    }
""",
            _ => string.Empty
        };

    private static string GenerateModuleListInformationFiles(IResource resource, IDictionary<MethodName, FrozenSet<ParameterName>> methodParameters) =>
        resource switch
        {
            _ when MethodExists(methodName: "ListInformationFiles",
                                parameterNames: ["serviceDirectory"],
                                methodParameters) => string.Empty,
            IResourceWithInformationFile resourceWithInformationFile =>
$$"""
    public static IEnumerable<{{resourceWithInformationFile.InformationFileType}}> ListInformationFiles(ServiceDirectory serviceDirectory) =>
        ListDirectories(serviceDirectory)
            .Select(directory => {{resourceWithInformationFile.InformationFileType}}.From(directory.Name, serviceDirectory))
            .Where(informationFile => informationFile.ToFileInfo().Exists());
""",
            _ => string.Empty
        };

    private static string GenerateModuleWriteDto(IResource resource, IDictionary<MethodName, FrozenSet<ParameterName>> methodParameters) =>
        resource switch
        {
            _ when MethodExists(methodName: "WriteDto",
                                parameterNames: ["uri", "dto", "pipeline", "cancellationToken"],
                                methodParameters) => string.Empty,
            IResourceWithInformationFile resourceWithInformationFile =>
$$"""
    public static async ValueTask WriteDto(this {{resourceWithInformationFile.InformationFileType}} file, {{resourceWithInformationFile.DtoType}} dto, CancellationToken cancellationToken) =>
        await file.ToFileInfo().OverwriteWithJson(dto, cancellationToken);
""",
            _ => string.Empty
        };

    private static string GenerateModuleReadDto(IResource resource, IDictionary<MethodName, FrozenSet<ParameterName>> methodParameters) =>
        resource switch
        {
            _ when MethodExists(methodName: "ReadDto",
                                parameterNames: ["uri", "pipeline", "cancellationToken"],
                                methodParameters) => string.Empty,
            IResourceWithInformationFile resourceWithInformationFile =>
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