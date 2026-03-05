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
using System.Threading;

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
    public abstract static Gen<ImmutableHashSet<T>> GenerateUpdates(IEnumerable<T> tModels, IEnumerable<ITestModel> allModels);
    public abstract static Gen<ImmutableHashSet<T>> GenerateNextState(IEnumerable<ITestModel> previousModels, IEnumerable<ITestModel> accumulatedNextModels);
}

internal delegate Gen<TestState> GenerateTestState();
internal delegate Gen<TestState> GenerateUpdatedSubsetOfTestState(TestState current);
internal delegate Gen<TestState> GenerateNextTestState(TestState current);

internal sealed record TestState
{
    public required ImmutableArray<ITestModel> Models { get; init; }

    public override string ToString() =>
        Models.Select(model =>
        {
            var dto = model.ToDto();

            if (model is ApiModel apiModel)
            {
                apiModel.Specification
                        .Iter(specification => dto.SetProperty("specification", specification));
            }

            return new JsonObject
            {
                [$"{model.Key}"] = dto
            };
        }).ToJsonArray().ToJsonString();
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

    public static void ConfigureGenerateUpdatedSubsetOfTestState(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureSortResources(builder);
        builder.TryAddSingleton(ResolveGenerateUpdatedSubsetOfTestState);
    }

    private static GenerateUpdatedSubsetOfTestState ResolveGenerateUpdatedSubsetOfTestState(IServiceProvider provider)
    {
        var sortResources = provider.GetRequiredService<SortResources>();

        var sortedTypes = sortResources(TestsModule.ResourceModels.Keys)
                              .Select(resource => TestsModule.ResourceModels[resource])
                              .ToImmutableArray();

        return current =>
        {
            var currentKeys = current.Models
                                     .Select(model => model.Key)
                                     .ToImmutableHashSet();

            return from next in sortedTypes.Aggregate(Gen.Const((UpdatedSubset: ImmutableArray<ITestModel>.Empty,
                                                                All: current.Models)),
                                                      (accumulator, type) =>
                                                        from tuple in accumulator
                                                        let currentUpdatedSubset = tuple.UpdatedSubset
                                                        let currentAll = tuple.All
                                                        // Generate updates for a subset of the resource type
                                                        from typeUpdatedSet in InvokeGenericMethod<Gen<ImmutableHashSet<ITestModel>>>(nameof(GenerateUpdatesOf),
                                                                                                                                      type,
                                                                                                                                      currentAll)
                                                        // Ensure that no keys have changed. Updates shouldn't modify keys
                                                        let typeUpdatedKeys = ImmutableHashSet.CreateRange(typeUpdatedSet.Select(model => model.Key))
                                                        let _ = typeUpdatedKeys.Traverse(key => currentKeys.Contains(key)
                                                                                                    ? Result.Success(Unit.Instance)
                                                                                                    : Error.From($"Key '{key}' is not present in the current test state."),
                                                                                         CancellationToken.None)
                                                                               .IfErrorThrow()
                                                        // Build the next state
                                                        let nextUpdatedSubset = currentUpdatedSubset.AddRange(typeUpdatedSet)
                                                        let nextAll = currentAll.Where(model => typeUpdatedKeys.Contains(model.Key) is false)
                                                                                .Concat(typeUpdatedSet)
                                                                                .ToImmutableArray()
                                                        select (nextUpdatedSubset, nextAll))
                   select new TestState
                   {
                       Models = next.UpdatedSubset
                   };
        };
    }

    private static Gen<ImmutableHashSet<ITestModel>> GenerateUpdatesOf<T>(IEnumerable<ITestModel> currentModels) where T : ITestModel<T> =>
        from next in T.GenerateUpdates(currentModels.OfType<T>(), currentModels)
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
                              .Select(resource => TestsModule.ResourceModels[resource])
                              .ToImmutableArray();

        return current =>
            from next in sortedTypes.Aggregate(Gen.Const(ImmutableArray<ITestModel>.Empty),
                                               (accumulator, type) =>
                                                from nextSet in accumulator
                                                from typeNextSet in InvokeGenericMethod<Gen<ImmutableHashSet<ITestModel>>>(nameof(GenerateNextStateOf), type, current.Models, nextSet)
                                                select nextSet.AddRange(typeNextSet))
            let normalizedNext = normalizeModels(next)
            select new TestState
            {
                Models = [.. normalizedNext]
            };

        IEnumerable<ITestModel> normalizeModels(IEnumerable<ITestModel> next)
        {
            var normalizedNext = removeUnreferencedVersionSets(next);

            return normalizedNext;
        }
        
        // Remove version sets that are not referenced by any API.
        // APIM automtically deletes the last version set, and keeping them in the test state
        // will lead to false negatives.
        IEnumerable<ITestModel> removeUnreferencedVersionSets(IEnumerable<ITestModel> models)
        {
            var referencedVersionSets = models.OfType<ApiModel>()
                                              .Choose(api => api.VersionSetName)
                                              .ToImmutableHashSet();

            return models.Where(model => model is not VersionSetModel versionSet
                                         || referencedVersionSets.Contains(versionSet.Key.Name));
        }
    }

    private static Gen<ImmutableHashSet<ITestModel>> GenerateNextStateOf<T>(IEnumerable<ITestModel> previousModels, IEnumerable<ITestModel> accumulatedNextModels) where T : ITestModel<T> =>
        from next in T.GenerateNextState(previousModels, accumulatedNextModels)
        select next.Cast<ITestModel>()
                   .ToImmutableHashSet();
}
