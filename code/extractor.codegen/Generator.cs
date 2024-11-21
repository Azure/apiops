using System;
using System.Collections.Frozen;
using System.Linq;
using System.Threading;
using codegen.resources;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace extractor.codegen;

[Generator]
public class CommonGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context) =>
        context.RegisterSourceOutput(GetProvider(context),
                                     (context, source) => GenerateExtractorContent(context, source));

    private static IncrementalValuesProvider<OutputSource> GetProvider(IncrementalGeneratorInitializationContext context) =>
        context.SyntaxProvider
               .CreateSyntaxProvider(IsInScope, GetMonitoredNode)
               .Collect()
               .SelectMany((monitoredNodes, _) =>
               {
                   var delegateNames = monitoredNodes.Where(node => node is MonitoredNode.Delegate)
                                                     .Select(node => ((MonitoredNode.Delegate)node).Name)
                                                     .ToFrozenSet();

                   var parameterDictionary = monitoredNodes.Where(node => node is MonitoredNode.ModuleParameter)
                                                           .Select(node => (MonitoredNode.ModuleParameter)node)
                                                           .GroupBy(node => node.ClassName)
                                                           .ToFrozenDictionary(group => group.Key,
                                                                               group => group.GroupBy(node => node.MethodName)
                                                                                             .ToFrozenDictionary(methodGroup => methodGroup.Key,
                                                                                                                 methodGroup => methodGroup.Select(node => node.ParameterName)
                                                                                                                                           .ToFrozenSet()));

                   return ApimResources.All
                                       .Select(resource => new OutputSource
                                       {
                                           Resource = resource,
                                           DelegateNames = delegateNames,
                                           ModuleMethodParameters = parameterDictionary.TryGetValue(resource.ModuleType, out var methodParameters)
                                                                     ? methodParameters
                                                                     : FrozenDictionary<string, FrozenSet<string>>.Empty
                                       });
               });

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

    private static void GenerateExtractorContent(SourceProductionContext context, OutputSource source) =>
        context.AddSource($"{source.Resource.GetType().Name}.extractor.g.cs",
                          GenerateExtractorContent(source));

    private static string GenerateExtractorContent(OutputSource source) =>
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

{{GenerateExtractDelegate(source)}}
{{GenerateListDelegate(source)}}
{{GenerateWriteArtifactsDelegate(source)}}
{{GenerateWriteInformationFileDelegate(source)}}

{{GenerateModule(source)}}
""";

    private static string GenerateExtractDelegate(OutputSource source) =>
        source switch
        {
            _ when source.DelegateNames.Contains($"Extract{source.Resource.PluralDescription}") => string.Empty,
            _ =>
$"""
public delegate ValueTask Extract{source.Resource.PluralDescription}(CancellationToken cancellationToken);
"""
        };

    private static string GenerateListDelegate(OutputSource source) =>
        source switch
        {
            _ when source.DelegateNames.Contains($"List{source.Resource.PluralDescription}") => string.Empty,
            { Resource: IResourceWithDto resourceWithDto } =>
$"""
public delegate IAsyncEnumerable<({resourceWithDto.NameType} Name, {resourceWithDto.DtoType} Dto)> List{source.Resource.PluralDescription}(CancellationToken cancellationToken);
""",
            _ => string.Empty
        };

    private static string GenerateWriteArtifactsDelegate(OutputSource source) =>
        source switch
        {
            _ when source.DelegateNames.Contains($"Write{source.Resource.SingularDescription}Artifacts") => string.Empty,
            { Resource: IResourceWithDto resourceWithDto } =>
$"""
public delegate ValueTask Write{source.Resource.SingularDescription}Artifacts({resourceWithDto.NameType} name, {resourceWithDto.DtoType} dto, CancellationToken cancellationToken);
""",
            _ => string.Empty
        };

    private static string GenerateWriteInformationFileDelegate(OutputSource source) =>
        source switch
        {
            _ when source.DelegateNames.Contains($"Write{source.Resource.SingularDescription}InformationFile") => string.Empty,
            { Resource: IResourceWithInformationFile resourceWithInformationFile } =>
$"""
public delegate ValueTask Write{source.Resource.SingularDescription}InformationFile({resourceWithInformationFile.NameType} name, {resourceWithInformationFile.DtoType} dto, CancellationToken cancellationToken);
""",
            _ => string.Empty
        };

    private static string GenerateModule(OutputSource source) =>
$$"""
internal static partial class {{source.Resource.ModuleType}}
{
{{GenerateConfigureExtract(source)}}

{{GenerateGetExtract(source)}}

{{GenerateConfigureList(source)}}

{{GenerateGetList(source)}}

{{GenerateConfigureWriteArtifacts(source)}}

{{GenerateGetWriteArtifacts(source)}}
}
""";

    private static string GenerateConfigureExtract(OutputSource source) =>
        source switch
        {
            _ when source.DelegateNames.Contains($"ConfigureExtract{source.Resource.PluralDescription}") => string.Empty,
            _ =>
$$"""
    public static void ConfigureExtract{{source.Resource.PluralDescription}}(IHostApplicationBuilder builder)
    {
        ConfigureList{{source.Resource.PluralDescription}}(builder);
        ConfigureWrite{{source.Resource.SingularDescription}}Artifacts(builder);

        builder.Services.TryAddSingleton(GetExtract{{source.Resource.PluralDescription}});
    }
"""
        };

    private static string GenerateGetExtract(OutputSource source) =>
        source switch
        {
            _ when source.DelegateNames.Contains($"GetExtract{source.Resource.PluralDescription}") => string.Empty,
            _ =>
    $$"""
    private static Extract{{source.Resource.PluralDescription}} GetExtract{{source.Resource.PluralDescription}}(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<List{{source.Resource.PluralDescription}}>();
        var writeArtifacts = provider.GetRequiredService<Write{{source.Resource.SingularDescription}}Artifacts>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(Extract{{source.Resource.PluralDescription}}));

            logger.LogInformation("Extracting {{source.Resource.LoggerPluralDescription}}...");

            await list(cancellationToken)
                    .IterParallel(async resource => await writeArtifacts(resource.Name, resource.Dto, cancellationToken),
                                  cancellationToken);
        };
    }
"""
        };

    private static string GenerateConfigureList(OutputSource source) =>
        source switch
        {
            _ when source.DelegateNames.Contains($"ConfigureList{source.Resource.PluralDescription}") => string.Empty,
            { Resource: IResourceWithDto } =>
$$"""
    private static void ConfigureList{{source.Resource.PluralDescription}}(IHostApplicationBuilder builder)
    {
        ConfigurationModule.ConfigureConfigurationJson(builder);
        ServiceModule.ConfigureServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetList{{source.Resource.PluralDescription}});
    }
""",
            _ => string.Empty
        };

    private static string GenerateGetList(OutputSource source) =>
        source switch
        {
            _ when source.DelegateNames.Contains($"GetList{source.Resource.PluralDescription}") => string.Empty,
            { Resource: IResourceWithDto resourceWithDto } =>
$$"""
    private static List{{source.Resource.PluralDescription}} GetList{{source.Resource.PluralDescription}}(IServiceProvider provider)
    {
        var configurationJson = provider.GetRequiredService<ConfigurationJson>();
        var serviceUri = provider.GetRequiredService<ServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        var configurationJsonObject = configurationJson.ToJsonObject();

        return cancellationToken =>
            findConfigurationNames()
                .Map(names => listFromSet(names, cancellationToken))
                .IfNone(() => listAll(cancellationToken));

        Option<IEnumerable<{{resourceWithDto.NameType}}>> findConfigurationNames()
        {
            var nameStringsResult = from jsonArray in configurationJsonObject.GetJsonArrayProperty("{{source.Resource.NameTypePluralCamelCase}}")
                                    from jsonValues in jsonArray.AsIterable()
                                                                .Traverse(node => node.AsJsonValue())
                                                                .As()
                                    from nameStrings in jsonValues.Traverse(value => value.AsString())
                                                                  .As()
                                    select nameStrings;

            var result = from nameStrings in nameStringsResult.ToFin()
                         from names in nameStrings.Traverse({{resourceWithDto.NameType}}.From)
                                                  .As()
                         select names.AsEnumerable();

            return result.ToOption();
        }

        IAsyncEnumerable<({{resourceWithDto.NameType}} Name, {{resourceWithDto.DtoType}} Dto)> listFromSet(IEnumerable<{{resourceWithDto.NameType}}> names, CancellationToken cancellationToken) =>
            names.Select(name => {{resourceWithDto.UriType}}.From(name, serviceUri))
                 .ToAsyncEnumerable()
                 .Choose(async uri =>
                 {
                     var dtoOption = await uri.GetOptionalDto(pipeline, cancellationToken);

                     return from dto in dtoOption
                            select (uri.Name, dto);
                 });

        IAsyncEnumerable<({{resourceWithDto.NameType}} Name, {{resourceWithDto.DtoType}} Dto)> listAll(CancellationToken cancellationToken)
        {
            var collectionUri = {{resourceWithDto.CollectionUriType}}.From(serviceUri);

            return collectionUri.List(pipeline, cancellationToken);
        }
    }
""",
            _ => string.Empty
        };

    private static string GenerateConfigureWriteArtifacts(OutputSource source) =>
        source switch
        {
            _ when source.DelegateNames.Contains($"Write{source.Resource.SingularDescription}Artifacts") => string.Empty,
            { Resource: IResourceWithDto } =>
$$"""
    private static void ConfigureWrite{{source.Resource.SingularDescription}}Artifacts(IHostApplicationBuilder builder)
    {
        ServiceModule.ConfigureServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetWrite{{source.Resource.SingularDescription}}Artifacts);
    }
""",
            _ => string.Empty
        };

    private static string GenerateGetWriteArtifacts(OutputSource source) =>
        source switch
        {
            _ when source.DelegateNames.Contains($"Write{source.Resource.SingularDescription}Artifacts") => string.Empty,
            { Resource: IResourceWithInformationFile resourceWithInformationFile } =>
$$"""
    private static Write{{source.Resource.SingularDescription}}Artifacts GetWrite{{source.Resource.SingularDescription}}Artifacts(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ServiceDirectory>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, cancellationToken) =>
        {
            var informationFile = {{resourceWithInformationFile.InformationFileType}}.From(name, serviceDirectory);

            logger.LogInformation("Writing {{source.Resource.LoggerSingularDescription}} information file {{{resourceWithInformationFile.InformationFileType}}}...", informationFile);
            await informationFile.WriteDto(dto, cancellationToken);
        };
    }
""",
            _ => string.Empty
        };
}