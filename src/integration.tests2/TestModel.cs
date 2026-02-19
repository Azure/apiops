using common;
using CsCheck;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
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
    public abstract static Gen<ImmutableHashSet<T>> SetGenerator { get; }
    public abstract static Gen<ImmutableHashSet<T>> GenerateUpdates(IEnumerable<T> models);
    public abstract static Gen<ImmutableHashSet<T>> GenerateNextState(IEnumerable<T> models);
}

/// <summary>
/// The complete test state: a coherent graph of all resource instances
/// generated for a test run.
/// </summary>
internal sealed record TestState
{
    /// <summary>
    /// All test models, in topological order (predecessors before successors).
    /// </summary>
    public required ImmutableArray<ITestModel> Models { get; init; }

    public static Gen<TestState> Generator { get; } =
        from tags in TagModel.SetGenerator
        select new TestState
        {
            Models = [.. tags]
        };

    public static Gen<TestState> GenerateNextState(TestState current) =>
        from next in common.tests.Generator.Traverse(TestsModule.ResourceModels.Values, type =>
        {
            var method = typeof(TestState).GetMethod(nameof(GenerateNextStateOf),
                                                     System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                          ?? throw new InvalidOperationException($"Failed to find method '{nameof(GenerateNextStateOf)}' on type '{typeof(TestState)}'.");

            var genericMethod = method.MakeGenericMethod(type) ?? throw new InvalidOperationException($"Failed to construct generic method for type '{type}'.");

            var subsetGen = genericMethod.Invoke(obj: default, parameters: [current.Models])
                            ?? throw new InvalidOperationException($"Failed to invoke method '{method.Name}' for type '{type}'.");

            return (Gen<ImmutableHashSet<ITestModel>>)subsetGen;
        })
        select new TestState
        {
            Models = [.. next.SelectMany(models => models)]
        };

    private static Gen<ImmutableHashSet<ITestModel>> GenerateNextStateOf<T>(IEnumerable<ITestModel> models) where T : ITestModel<T> =>
        from next in T.GenerateNextState(models.OfType<T>())
        select next.Cast<ITestModel>()
                   .ToImmutableHashSet();

    public override string ToString() =>
        Models.Select(model => JsonValue.Create($"{model.Key}"))
              .ToJsonArray()
              .ToJsonString();
}
