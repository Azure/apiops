//using codegen.resources;
//using Microsoft.CodeAnalysis;
//using Microsoft.CodeAnalysis.CSharp;
//using Microsoft.CodeAnalysis.CSharp.Syntax;
//using System;
//using System.Collections.Frozen;
//using System.Collections.Generic;
//using System.Linq;
//using System.Threading;

//namespace common.codegen;

//[Generator]
//public class CommonGenerator : IIncrementalGenerator
//{
//    public void Initialize(IncrementalGeneratorInitializationContext context) =>
//        context.RegisterSourceOutput(GetProvider(context), GenerateContent);

//    private static IncrementalValuesProvider<OutputSource> GetProvider(IncrementalGeneratorInitializationContext context) =>
//        context.SyntaxProvider
//               .CreateSyntaxProvider(IsInScope, GetModuleParameter)
//               .Collect()
//               .SelectMany((moduleParameters, _) => GetOutputSources(ApimResources.All, moduleParameters));

//    private static bool IsInScope(SyntaxNode syntaxNode, CancellationToken _) =>
//        syntaxNode switch
//        {
//            ParameterSyntax
//            {
//                Parent: ParameterListSyntax
//                {
//                    Parent: MethodDeclarationSyntax
//                    {
//                        Parent: ClassDeclarationSyntax classDeclarationSyntax
//                    }
//                }
//            } => classDeclarationSyntax.Modifiers.Any(SyntaxKind.StaticKeyword)
//                 && (classDeclarationSyntax.Modifiers.Any(SyntaxKind.PublicKeyword)
//                     || classDeclarationSyntax.Modifiers.Any(SyntaxKind.InternalKeyword))
//                 && classDeclarationSyntax.Identifier.ToString().EndsWith("Module"),
//            _ => false
//        };

//    private static ModuleParameter GetModuleParameter(GeneratorSyntaxContext context, CancellationToken _) =>
//        context.Node switch
//        {
//            ParameterSyntax
//            {
//                Parent: ParameterListSyntax
//                {
//                    Parent: MethodDeclarationSyntax
//                    {
//                        Parent: ClassDeclarationSyntax classDeclarationSyntax
//                    } methodDeclarationSyntax
//                }
//            } parameterSyntax => new ModuleParameter
//            {
//                ClassName = classDeclarationSyntax.Identifier.ToString(),
//                MethodName = methodDeclarationSyntax.Identifier.ToString(),
//                ParameterName = parameterSyntax.Identifier.ToString()
//            },
//            _ => throw new InvalidOperationException($"Node type {context.Node.GetType()} is not supported.")
//        };

//    private static IEnumerable<OutputSource> GetOutputSources(IEnumerable<IResource> resources, IEnumerable<ModuleParameter> moduleParameters)
//    {
//        var parameterDictionary = GetClassModuleParameterDictionary(moduleParameters);

//        return resources.Select(resource => new OutputSource
//        {
//            Resource = resource,
//            ModuleMethodParameters = parameterDictionary.TryGetValue(resource.ModuleType, out var methodParameters)
//                                     ? methodParameters
//                                     : FrozenDictionary<string, FrozenSet<string>>.Empty
//        });
//    }

//    private static FrozenDictionary<string, FrozenDictionary<string, FrozenSet<string>>> GetClassModuleParameterDictionary(IEnumerable<ModuleParameter> moduleParameters) =>
//        moduleParameters.GroupBy(node => node.ClassName)
//                        .ToFrozenDictionary(group => group.Key,
//                                            group => group.GroupBy(node => node.MethodName)
//                                                        .ToFrozenDictionary(methodGroup => methodGroup.Key,
//                                                                            methodGroup => methodGroup.Select(node => node.ParameterName)
//                                                                                                        .ToFrozenSet()));

//    private sealed record ModuleParameter
//    {
//        public required string ClassName { get; init; }
//        public required string MethodName { get; init; }
//        public required string ParameterName { get; init; }
//    }

//    private sealed record OutputSource
//    {
//        public required IResource Resource { get; init; }
//        public required FrozenDictionary<string, FrozenSet<string>> ModuleMethodParameters { get; init; }
//    }

//    private static void GenerateContent(SourceProductionContext context, OutputSource source) =>
//        context.AddSource($"{source.Resource.GetType().Name}.common.g.cs",
//                          GenerateContent(source));

//    private static string GenerateContent(OutputSource source) =>
//$$"""
//using Azure.Core.Pipeline;
//using Flurl;
//using LanguageExt;
//using System.Collections.Generic;
//using System;
//using System.Collections.Immutable;
//using System.IO;
//using System.Text.Json.Serialization;
//using System.Linq;
//using System.Threading;
//using System.Threading.Tasks;

//namespace common;

//#nullable enable

//public partial static class {{source.Resource.SingularDescription}}Module
//{
//}
//""";
//}