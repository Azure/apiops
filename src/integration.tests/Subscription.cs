using common;
using common.tests;
using CsCheck;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Nodes;

namespace integration.tests;

internal abstract record SubscriptionScope
{
    internal sealed record Product : SubscriptionScope
    {
        public required ResourceKey ProductKey { get; init; }
    }

    internal sealed record Api : SubscriptionScope
    {
        public required ResourceKey ApiKey { get; init; }
    }

    internal sealed record AllApis : SubscriptionScope
    {
        public static AllApis Instance { get; } = new();
    }
}

internal sealed record SubscriptionModel : ITestModel<SubscriptionModel>
{
    public required string DisplayName { get; init; }

    public required ResourceKey Key { get; init; }

    public required SubscriptionScope Scope { get; init; }

    public required string State { get; init; }

    public required bool AllowTracing { get; init; }

    public JsonObject ToDto() =>
        new()
        {
            ["properties"] = new JsonObject
            {
                ["displayName"] = DisplayName,
                ["allowTracing"] = AllowTracing,
                ["state"] = State,
                ["scope"] = Scope switch
                {
                    SubscriptionScope.Product product => $"/{product.ProductKey}",
                    SubscriptionScope.Api api => $"/{api.ApiKey}",
                    SubscriptionScope.AllApis => "/apis",
                    var scope => throw new InvalidOperationException($"Scope '{scope}' is not supported.")
                }
            }
        };

    public Result<Unit> ValidateDto(JsonObject dto)
    {
        return from _ in validateDisplayName()
               from __ in validateState()
               from ___ in validateAllowTracing()
               from ____ in validateScope()
               select Unit.Instance;

        Result<Unit> validateDisplayName() =>
            from properties in dto.GetJsonObjectProperty("properties")
            from displayName in properties.GetStringProperty("displayName")
            from unit in displayName == DisplayName
                        ? Result.Success(Unit.Instance)
                        : Error.From($"Resource '{Key}' has displayName '{displayName}' instead of '{DisplayName}'.")
            select unit;

        Result<Unit> validateState() =>
            from properties in dto.GetJsonObjectProperty("properties")
            from state in properties.GetStringProperty("state")
            from unit in state.Equals(State, StringComparison.OrdinalIgnoreCase)
                        ? Result.Success(Unit.Instance)
                        : Error.From($"Resource '{Key}' has state '{state}' instead of '{State}'.")
            select unit;

        Result<Unit> validateAllowTracing() =>
            from properties in dto.GetJsonObjectProperty("properties")
            from allowTracing in properties.GetBoolProperty("allowTracing")
            from unit in allowTracing == AllowTracing
                        ? Result.Success(Unit.Instance)
                        : Error.From($"Resource '{Key}' has allowTracing '{allowTracing}' instead of '{AllowTracing}'.")
            select unit;

        Result<Unit> validateScope() =>
            from properties in dto.GetJsonObjectProperty("properties")
            from scope in properties.GetStringProperty("scope")
            from unit in Scope switch
            {
                SubscriptionScope.Product product =>
                    scope.EndsWith($"{product.ProductKey}", StringComparison.OrdinalIgnoreCase)
                        ? Result.Success(Unit.Instance)
                        : Error.From($"Resource '{Key}' has scope '{scope}' instead of ending with '{product.ProductKey}'."),
                SubscriptionScope.Api api =>
                    scope.EndsWith($"{api.ApiKey}", StringComparison.OrdinalIgnoreCase)
                        ? Result.Success(Unit.Instance)
                        : Error.From($"Resource '{Key}' has scope '{scope}' instead of ending with '{api.ApiKey}'."),
                SubscriptionScope.AllApis =>
                    scope.EndsWith("apis", StringComparison.OrdinalIgnoreCase)
                        ? Result.Success(Unit.Instance)
                        : Error.From($"Resource '{Key}' has scope '{scope}' instead of ending with 'apis'."),
                var scope => Error.From($"Scope '{scope}' is not supported.")
            }
            select unit;
    }

    public static Gen<ImmutableHashSet<SubscriptionModel>> GenerateSet(IEnumerable<ITestModel> models) =>
        from set in Generate(models).HashSetOf(0, 5)
        select ToSet(set);

    private static Gen<SubscriptionModel> Generate(IEnumerable<ITestModel> models) =>
        from name in Generator.ResourceName
        from displayName in CommonModule.GenerateDisplayName(name)
        from scope in GenerateScope(models)
        from state in GenerateState()
        from allowTracing in Gen.Bool
        select new SubscriptionModel
        {
            DisplayName = displayName,
            Key = ResourceKey.From(SubscriptionResource.Instance, name),
            Scope = scope,
            State = state,
            AllowTracing = allowTracing
        };

    private static ImmutableHashSet<SubscriptionModel> ToSet(IEnumerable<SubscriptionModel> models) =>
        [.. models.DistinctBy(model => model.Key)];

    private static Gen<SubscriptionScope> GenerateScope(IEnumerable<ITestModel> models)
    {
        var allApisScopeGen = Gen.Const(SubscriptionScope.AllApis.Instance as SubscriptionScope);

        var modelsArray = models.ToImmutableArray();

        var products = modelsArray.OfType<ProductModel>()
                                  .Select(model => model.Key)
                                  .ToImmutableArray();

        var getProductScopeGen = () => from key in Gen.OneOfConst([.. products])
                                       select new SubscriptionScope.Product
                                       {
                                           ProductKey = key
                                       } as SubscriptionScope;

        var apis = modelsArray.OfType<ApiModel>()
                              .Where(model => model.Key.Name == model.RootName)
                              .Select(model => model.Key)
                              .ToImmutableArray();

        var getApiScopeGen = () => from key in Gen.OneOfConst([.. apis])
                                   select new SubscriptionScope.Api
                                   {
                                       ApiKey = key
                                   } as SubscriptionScope;

        return (products, apis) switch
        {
            ([], []) => allApisScopeGen,
            (_, []) => Gen.OneOf(allApisScopeGen, getProductScopeGen()),
            ([], _) => Gen.OneOf(allApisScopeGen, getApiScopeGen()),
            _ => Gen.OneOf(allApisScopeGen, getProductScopeGen(), getApiScopeGen())
        };
    }

    private static Gen<string> GenerateState() =>
        Gen.OneOfConst("active", "suspended");

    public static Gen<ImmutableHashSet<SubscriptionModel>> GenerateUpdates(IEnumerable<SubscriptionModel> subscriptionModels, IEnumerable<ITestModel> allModels) =>
        from updatedModels in Generator.Traverse(subscriptionModels,
                                                 model => GenerateUpdate(model, allModels))
        let updatedSet = ToSet(updatedModels)
        where updatedSet.Count == updatedModels.Length
        select updatedSet;

    private static Gen<SubscriptionModel> GenerateUpdate(SubscriptionModel model, IEnumerable<ITestModel> allModels) =>
        from displayName in CommonModule.GenerateDisplayName(model.Key.Name, model.DisplayName)
        from state in GenerateState()
        from allowTracing in Gen.Bool
        from scope in GenerateScope(allModels)
        select model with
        {
            DisplayName = displayName,
            State = state,
            AllowTracing = allowTracing,
            Scope = scope
        };

    public static Gen<ImmutableHashSet<SubscriptionModel>> GenerateNextState(IEnumerable<ITestModel> previousModels, IEnumerable<ITestModel> accumulatedNextModels)
    {
        var validScopes =
            accumulatedNextModels.Choose(model => model switch
                                  {
                                      ProductModel product => Option.Some<SubscriptionScope>(new SubscriptionScope.Product
                                      {
                                          ProductKey = product.Key
                                      }),
                                      ApiModel api when api.Key.Name == api.RootName => new SubscriptionScope.Api
                                      {
                                          ApiKey = api.Key
                                      },
                                      _ => Option.None
                                  })
                                  .Append(SubscriptionScope.AllApis.Instance)
                                  .ToImmutableHashSet();

        var currentModels = previousModels.OfType<SubscriptionModel>();

        return from shuffled in Gen.Shuffle(currentModels.ToArray())
               from keptCount in Gen.Int[0, shuffled.Length]
               let kept = shuffled.Take(keptCount).ToImmutableArray()
               from unchangedCount in Gen.Int[0, kept.Length]
               let unchanged = kept.Take(unchangedCount)
                                   .Where(model => validScopes.Contains(model.Scope))
               let modelsToUpdate = kept.Skip(unchangedCount).ToImmutableArray()
               from changed in GenerateUpdates(modelsToUpdate, accumulatedNextModels)
               from added in GenerateSet(accumulatedNextModels)
               select ToSet([.. unchanged, .. changed, .. added]);
    }
}