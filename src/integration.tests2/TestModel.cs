using common;
using CsCheck;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;

namespace integration.tests;

/// <summary>
/// Base interface for all integration test models. Each model represents
/// a resource instance with known intended properties.
/// </summary>
internal interface ITestModel
{
    /// <summary>
    /// The resource type, name, and parent chain identifying this resource.
    /// </summary>
    ResourceKey Key { get; }

    /// <summary>
    /// Builds the JSON DTO payload for putting this resource to APIM.
    /// </summary>
    JsonObject ToDto();

    /// <summary>
    /// Validates that a DTO matches this model's intended properties.
    /// </summary>
    Result<Unit> ValidateDto(JsonObject dto);
}

internal interface ITestModel<T> : ITestModel
{
    public abstract static Gen<ImmutableHashSet<T>> GenerateSet(IEnumerable<ITestModel> models);
    public abstract static Gen<ImmutableHashSet<T>> GenerateUpdates(IEnumerable<T> models);
    public abstract static Gen<ImmutableHashSet<T>> GenerateNextState(IEnumerable<ITestModel> previousModels, IEnumerable<ITestModel> accumulatedNextModels);
}

internal delegate Gen<TestState> GenerateTestState();
internal delegate Gen<TestState> GenerateNextTestState(TestState current);

internal sealed record TestState
{
    public required ImmutableArray<ITestModel> Models { get; init; }

    public override string ToString() =>
        Models.Select(model => JsonValue.Create($"{model.Key}"))
              .ToJsonArray()
              .ToJsonString();
}

internal static class TestStateModule
{
    public static void ConfigureGenerateTestState(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureSortResources(builder);
        builder.TryAddSingleton(ResolveGenerateTestState);
    }

    private static GenerateTestState ResolveGenerateTestState(IServiceProvider provider)
    {
        var sortResources = provider.GetRequiredService<SortResources>();

        var sortedTypes = sortResources(TestsModule.ResourceModels.Keys)
                              .Select(resource => TestsModule.ResourceModels[resource]);

        return () =>
            from models in sortedTypes.Aggregate(Gen.Const(ImmutableArray<ITestModel>.Empty),
                                                 (accumulator, type) =>
                                                    from currentSet in accumulator
                                                    from newSet in InvokeGenericMethod<Gen<ImmutableHashSet<ITestModel>>>(nameof(GenerateSetOf), type, currentSet)
                                                    select currentSet.AddRange(newSet))
            select new TestState
            {
                Models = models
            };
    }

    private static TReturn InvokeGenericMethod<TReturn>(string methodName, Type genericType, params object[] parameters)
    {
        var method = typeof(TestStateModule).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)
                        ?? throw new InvalidOperationException($"Failed to find method '{methodName}' on type '{typeof(TestStateModule)}'.");

        var genericMethod = method.MakeGenericMethod(genericType)
                            ?? throw new InvalidOperationException($"Failed to construct generic method  '{methodName}' for type '{genericType}'.");

        return (TReturn)(genericMethod.Invoke(obj: default, parameters: parameters)
                            ?? throw new InvalidOperationException($"Method '{methodName}' for type '{genericType}' returned a null result."));
    }

    private static Gen<ImmutableHashSet<ITestModel>> GenerateSetOf<T>(IEnumerable<ITestModel> allModels) where T : ITestModel<T> =>
        from next in T.GenerateSet(allModels)
        select next.Cast<ITestModel>()
                   .ToImmutableHashSet();

    public static void ConfigureGenerateNextTestState(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureSortResources(builder);
        builder.TryAddSingleton(ResolveGenerateNextTestState);
    }

    private static GenerateNextTestState ResolveGenerateNextTestState(IServiceProvider provider)
    {
        var sortResources = provider.GetRequiredService<SortResources>();

        var sortedTypes = sortResources(TestsModule.ResourceModels.Keys)
                              .Select(resource => TestsModule.ResourceModels[resource]);

        return current =>
            from next in sortedTypes.Aggregate(Gen.Const(ImmutableArray<ITestModel>.Empty),
                                               (accumulator, type) =>
                                                from nextSet in accumulator
                                                from typeNextSet in InvokeGenericMethod<Gen<ImmutableHashSet<ITestModel>>>(nameof(GenerateNextStateOf), type, current.Models, nextSet)
                                                select nextSet.AddRange(typeNextSet))
            select new TestState
            {
                Models = next
            };
    }

    private static Gen<ImmutableHashSet<ITestModel>> GenerateNextStateOf<T>(IEnumerable<ITestModel> previousModels, IEnumerable<ITestModel> accumulatedNextModels) where T : ITestModel<T> =>
        from next in T.GenerateNextState(previousModels, accumulatedNextModels)
        select next.Cast<ITestModel>()
                   .ToImmutableHashSet();
}
