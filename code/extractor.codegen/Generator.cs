using codegen.resources;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace extractor.codegen;

[Generator]
public class CommonGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context) =>
        context.RegisterSourceOutput(GetProvider(context), GenerateContent);

    private static IncrementalValuesProvider<OutputSource> GetProvider(IncrementalGeneratorInitializationContext context) =>
        context.SyntaxProvider
               .CreateSyntaxProvider(IsInScope, GetMonitoredNode)
               .Collect()
               .SelectMany((monitoredNodes, _) => GetOutputSources(ApimResources.All, monitoredNodes));

    private static bool IsInScope(SyntaxNode syntaxNode, CancellationToken _) =>
        syntaxNode switch
        {
            DelegateDeclarationSyntax delegateDeclaration => delegateDeclaration.Modifiers.Any(SyntaxKind.PublicKeyword)
                                                             || delegateDeclaration.Modifiers.Any(SyntaxKind.InternalKeyword),
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
                 && classDeclarationSyntax.Identifier.ToString().EndsWith("Module"),
            _ => false
        };

    private static MonitoredNode GetMonitoredNode(GeneratorSyntaxContext context, CancellationToken _) =>
        context.Node switch
        {
            DelegateDeclarationSyntax delegateDeclaration => new MonitoredNode.Delegate
            {
                Name = delegateDeclaration.Identifier.ToString()
            },
            ParameterSyntax
            {
                Parent: ParameterListSyntax
                {
                    Parent: MethodDeclarationSyntax
                    {
                        Parent: ClassDeclarationSyntax classDeclarationSyntax
                    } methodDeclarationSyntax
                }
            } parameterSyntax => new MonitoredNode.ModuleParameter
            {
                ClassName = classDeclarationSyntax.Identifier.ToString(),
                MethodName = methodDeclarationSyntax.Identifier.ToString(),
                ParameterName = parameterSyntax.Identifier.ToString()
            },
            _ => throw new InvalidOperationException($"Node type {context.Node.GetType()} is not supported.")
        };

    private static IEnumerable<OutputSource> GetOutputSources(IEnumerable<IResource> resources, IEnumerable<MonitoredNode> monitoredNodes)
    {
        var delegateNames = GetDelegateNames(monitoredNodes);
        var parameterDictionary = GetClassModuleParameterDictionary(monitoredNodes);

        return resources.Select(resource => new OutputSource
        {
            Resource = resource,
            DelegateNames = delegateNames,
            ModuleMethodParameters = parameterDictionary.TryGetValue(resource.GetModuleClassName(), out var methodParameters)
                                      ? methodParameters
                                      : FrozenDictionary<string, FrozenSet<string>>.Empty
        });
    }

    private static FrozenSet<string> GetDelegateNames(IEnumerable<MonitoredNode> monitoredNodes) =>
        monitoredNodes.Where(node => node is MonitoredNode.Delegate)
                      .Select(node => ((MonitoredNode.Delegate)node).Name)
                      .ToFrozenSet();

    private static FrozenDictionary<string, FrozenDictionary<string, FrozenSet<string>>> GetClassModuleParameterDictionary(IEnumerable<MonitoredNode> monitoredNodes) =>
        monitoredNodes.Where(node => node is MonitoredNode.ModuleParameter)
                      .Select(node => (MonitoredNode.ModuleParameter)node)
                      .GroupBy(node => node.ClassName)
                      .ToFrozenDictionary(group => group.Key,
                                          group => group.GroupBy(node => node.MethodName)
                                                        .ToFrozenDictionary(methodGroup => methodGroup.Key,
                                                                            methodGroup => methodGroup.Select(node => node.ParameterName)
                                                                                                                                           .ToFrozenSet()));

    private abstract record MonitoredNode
    {
        public sealed record Delegate : MonitoredNode
        {
            public required string Name { get; init; }
        }

        public sealed record ModuleParameter : MonitoredNode
        {
            public required string ClassName { get; init; }
            public required string MethodName { get; init; }
            public required string ParameterName { get; init; }
        }
    }

    private sealed record OutputSource
    {
        public required IResource Resource { get; init; }
        public required FrozenSet<string> DelegateNames { get; init; }
        public required FrozenDictionary<string, FrozenSet<string>> ModuleMethodParameters { get; init; }
    }

    private static void GenerateContent(SourceProductionContext context, OutputSource source) =>
        context.AddSource($"{source.Resource.GetType().Name}.extractor.g.cs",
                          GenerateContent(source));

    private static string GenerateContent(OutputSource source) =>
$$"""
using Azure.Core.Pipeline;
using common;
using LanguageExt;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

{{GenerateDelegates(source)}}

{{GenerateModule(source)}}
""".RemoveExtraneousLinesFromCode();

    private static string GenerateDelegates(OutputSource source) =>
$"""
{GenerateExtractDelegate(source)}

{GenerateListDelegate(source)}

{GenerateWriteInformationFileDelegate(source)}

{GenerateWritePolicyFileDelegate(source)}
""".WithMaxBlankLines(0);

    private static string GenerateExtractDelegate(OutputSource source)
    {
        switch (source)
        {
            case object when source.DelegateNames.Contains($"Extract{source.Resource.PluralDescription}"):
                return string.Empty;
            case { Resource: var resource }:
                var typedParameters =
                    resource.GetNameTypeWithParameterHierarchy()
                            .Skip(1)
                            .Concat(["CancellationToken cancellationToken"])
                            .CommaSeparate();

                return
$"""
public delegate ValueTask Extract{resource.PluralDescription}({typedParameters});
""";
            default:
                return string.Empty;
        };
    }

    private static string GenerateListDelegate(OutputSource source)
    {
        switch (source)
        {
            case object when source.DelegateNames.Contains($"List{source.Resource.PluralDescription}"):
                return string.Empty;
            case { Resource: var resource }:
                var typedParameters =
                    resource.GetNameTypeWithParameterHierarchy()
                            .Skip(1)
                            .Concat(["CancellationToken cancellationToken"])
                            .CommaSeparate();

                return resource switch
                {
                    IResourceWithDto resourceWithDto and IResourceWithName resourceWithName =>
$"""
public delegate IAsyncEnumerable<({resourceWithName.NameType} Name, {resourceWithDto.DtoType} Dto)> List{resource.PluralDescription}({typedParameters});
""",
                    IResourceWithName resourceWithName =>
$"""
public delegate IAsyncEnumerable<{resourceWithName.NameType}> List{resource.PluralDescription}({typedParameters});
""",
                    _ => string.Empty
                };
            default:
                return string.Empty;
        };
    }

    private static string GenerateWriteInformationFileDelegate(OutputSource source)
    {
        switch (source)
        {
            case object when source.DelegateNames.Contains($"Write{source.Resource.SingularDescription}InformationFile"):
                return string.Empty;
            case { Resource: IResourceWithInformationFile resource }
                when source.DelegateNames.Contains($"Write{resource.InformationFileType}"):
                return string.Empty;
            case { Resource: IResourceWithInformationFile resource }:
                var typedParameters =
                    resource.GetNameTypeWithParameterHierarchy()
                            .Concat(["CancellationToken cancellationToken"])
                            .Prepend($"{resource.DtoType} dto")
                            .CommaSeparate();

                return
$"""
public delegate ValueTask Write{resource.InformationFileType}({typedParameters});
""";
            default:
                return string.Empty;
        };
    }

    private static string GenerateWritePolicyFileDelegate(OutputSource source)
    {
        switch (source)
        {
            case object when source.DelegateNames.Contains($"Write{source.Resource.SingularDescription}PolicyFile"):
                return string.Empty;
            case { Resource: IPolicyResource resource }
                when source.DelegateNames.Contains($"Write{resource.PolicyFileType}"):
                return string.Empty;
            case { Resource: IPolicyResource resource }:
                var typedParameters =
                    resource.GetNameTypeWithParameterHierarchy()
                            .Skip(1)
                            .Concat(["CancellationToken cancellationToken"])
                            .Prepend($"{resource.DtoType} dto")
                            .CommaSeparate();

                return
$"""
public delegate ValueTask Write{resource.PolicyFileType}({typedParameters});
""";
            default:
                return string.Empty;
        };
    }

    private static bool MethodExists(string methodName, IEnumerable<string> parameterNames, IDictionary<string, FrozenSet<string>> methodParameters) =>
        methodParameters.TryGetValue(methodName, out var parameters)
        && parameterNames.All(parameters.Contains);

    private static string GenerateModule(OutputSource source) =>
$$"""
internal static partial class {{source.Resource.GetModuleClassName()}}
{
{{GenerateConfigureExtract(source)}}

{{GenerateGetExtract(source)}}

{{GenerateConfigureList(source)}}

{{GenerateGetList(source)}}
}
""";

    private static string GenerateConfigureExtract(OutputSource source) =>
        source switch
        {
            _ when MethodExists(methodName: $"ConfigureExtract{source.Resource.PluralDescription}",
                                parameterNames: ["builder"],
                                source.ModuleMethodParameters) => string.Empty,
            { Resource: IResource resource } =>
$$"""
    public static void ConfigureExtract{{resource.PluralDescription}}(IHostApplicationBuilder builder)
    {
{{$"""
        ConfigureList{resource.PluralDescription}(builder);

{resource switch
{
    IPolicyResource policyResource =>
$"""
        ConfigureWrite{policyResource.PolicyFileType}(builder);
""",
    _ => string.Empty
}}

{resource switch
{
    IResourceWithInformationFile resourceWithInformationFile =>
$"""
        ConfigureWrite{resourceWithInformationFile.InformationFileType}(builder);
""",
    _ => string.Empty
}}
""".WithMaxBlankLines(0)}}

        builder.Services.TryAddSingleton(GetExtract{{resource.PluralDescription}});
    }
"""
        };

    private static string GenerateGetExtract(OutputSource source) =>
        source switch
        {
            _ when MethodExists(methodName: $"GetExtract{source.Resource.PluralDescription}",
                                parameterNames: ["provider"],
                                source.ModuleMethodParameters) => string.Empty,
            { Resource: IResource resource } =>
$$"""
    private static Extract{{resource.PluralDescription}} GetExtract{{resource.PluralDescription}}(IServiceProvider provider)
    {
{{$"""
        var list = provider.GetRequiredService<List{resource.PluralDescription}>();

{resource switch
{
    IPolicyResource policyResource =>
$"""
        var writePolicyFile = provider.GetRequiredService<Write{policyResource.PolicyFileType}>();
""",
    IResourceWithInformationFile resourceWithInformationFile =>
$"""
        var writeInformationFile = provider.GetRequiredService<Write{resourceWithInformationFile.InformationFileType}>();
""",
    _ => string.Empty
}}
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();
""".WithMaxBlankLines(0)}}

        return {{resource.GetNameTypeParameterHierarchy().Skip(1).ToArray() switch
{
[] => "async cancellationToken =>",
    var values => $"async ({values.Concat(["cancellationToken"]).CommaSeparate()}) =>"
}}}
        {
            using var _ = activitySource.StartActivity(nameof(Extract{{resource.PluralDescription}}));

            logger.LogInformation("Extracting {{resource.LoggerPluralDescription}}...");

            await list(cancellationToken)
                    .IterParallel({{resource switch
{
    IResourceWithDto and IResourceWithName => "async resource => await extract(resource.Name, resource.Dto, cancellationToken),",
    IResourceWithName => "async name => await extract(name, cancellationToken),",
    _ => string.Empty
}}}
                                  cancellationToken);
        };

        async ValueTask extract({{resource switch
{
    IResourceWithDto resourceWithDto and IResourceWithName resourceWithName => $"{resourceWithName.NameType} name, {resourceWithDto.DtoType} dto, ",
    IResourceWithName resourceWithName => $"{resourceWithName.NameType} name, ",
    _ => string.Empty
}}}CancellationToken cancellationToken)
        {
{{resource switch
{
    IPolicyResource =>
$"""
            await writePolicyFile(dto, name, cancellationToken);
""",
    IResourceWithInformationFile =>
$"""
            await writeInformationFile(dto, name, cancellationToken);
""",
    _ => string.Empty
}}}
        }
    }
"""
        };

    private static string GenerateConfigureList(OutputSource source) =>
        source switch
        {
            _ when MethodExists(methodName: $"ConfigureList{source.Resource.PluralDescription}",
                                parameterNames: ["builder"],
                                source.ModuleMethodParameters) => string.Empty,
            { Resource: var resource } =>
$$"""
    private static void ConfigureList{{resource.PluralDescription}}(IHostApplicationBuilder builder)
    {
        ConfigurationModule.ConfigureConfigurationJson(builder);
        ServiceModule.ConfigureServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetList{{resource.PluralDescription}});
    }
""",
            _ => string.Empty
        };

    private static string GenerateGetList(OutputSource source) =>
        source switch
        {
            _ when MethodExists(methodName: $"GetList{source.Resource.PluralDescription}",
                                parameterNames: ["provider"],
                                source.ModuleMethodParameters) => string.Empty,
            { Resource: IResource resource } =>
$$"""
    private static List{{resource.PluralDescription}} GetList{{resource.PluralDescription}}(IServiceProvider provider)
    {
        var configurationJson = provider.GetRequiredService<ConfigurationJson>();
        var serviceUri = provider.GetRequiredService<ServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return ({{resource.GetNameTypeParameterHierarchy().Skip(1).Concat(["cancellationToken"]).CommaSeparate()}}) =>
        {
            var collectionUri = {{resource.CollectionUriType}}.From({{resource.GetNameTypeParameterHierarchy().Skip(1).Concat(["serviceUri"]).CommaSeparate()}});
            return collectionUri.{{resource switch
{
    IResourceWithDto => "List(pipeline, cancellationToken);",
    _ => "ListNames(pipeline, cancellationToken);"
}}}
        };
    }
"""
        };

    //    private static string GenerateGetList(OutputSource source) =>
    //        source switch
    //        {
    //            _ when source.DelegateNames.Contains($"GetList{source.Resource.PluralDescription}") => string.Empty,
    //            { Resource: IResourceWithName resourceWithDto } =>
    //$$"""
    //    private static List{{source.Resource.PluralDescription}} GetList{{source.Resource.PluralDescription}}(IServiceProvider provider)
    //    {
    //        var configurationJson = provider.GetRequiredService<ConfigurationJson>();
    //        var serviceUri = provider.GetRequiredService<ServiceUri>();
    //        var pipeline = provider.GetRequiredService<HttpPipeline>();

    //        var configurationJsonObject = configurationJson.ToJsonObject();

    //        return cancellationToken =>
    //            findConfigurationNames()
    //                .Map(names => listFromSet(names, cancellationToken))
    //                .IfNone(() => listAll(cancellationToken));

    //        Option<IEnumerable<{{resourceWithDto.NameTypePascalCase}}>> findConfigurationNames()
    //        {
    //            var nameStringsResult = from jsonArray in configurationJsonObject.GetJsonArrayProperty("{{source.Resource.NameTypePluralCamelCase}}")
    //                                    from jsonValues in jsonArray.AsIterable()
    //                                                                .Traverse(node => node.AsJsonValue())
    //                                                                .As()
    //                                    from nameStrings in jsonValues.Traverse(value => value.AsString())
    //                                                                  .As()
    //                                    select nameStrings;

    //            var result = from nameStrings in nameStringsResult.ToFin()
    //                         from names in nameStrings.Traverse({{resourceWithDto.NameTypePascalCase}}.From)
    //                                                  .As()
    //                         select names.AsEnumerable();

    //            return result.ToOption();
    //        }

    //        IAsyncEnumerable<({{resourceWithDto.NameTypePascalCase}} Name, {{resourceWithDto.DtoType}} Dto)> listFromSet(IEnumerable<{{resourceWithDto.NameTypePascalCase}}> names, CancellationToken cancellationToken) =>
    //            names.Select(name => {{resourceWithDto.UriType}}.From(name, serviceUri))
    //                 .ToAsyncEnumerable()
    //                 .Choose(async uri =>
    //                 {
    //                     var dtoOption = await uri.GetOptionalDto(pipeline, cancellationToken);

    //                     return from dto in dtoOption
    //                            select (uri.Name, dto);
    //                 });

    //        IAsyncEnumerable<({{resourceWithDto.NameTypePascalCase}} Name, {{resourceWithDto.DtoType}} Dto)> listAll(CancellationToken cancellationToken)
    //        {
    //            var collectionUri = {{resourceWithDto.CollectionUriType}}.From(serviceUri);

    //            return collectionUri.List(pipeline, cancellationToken);
    //        }
    //    }
    //""",
    //            _ => string.Empty
    //        };
}