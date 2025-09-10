using common;
using CsCheck;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Nodes;

namespace integration.tests;

file sealed record ApiRevision
{
    public required bool IsCurrent { get; init; }

    public required int Number
    {
        get;
        init => field = value < 1 ? throw new InvalidOperationException("Revision must be greater than 0.") : value;
    }

    public static Gen<ApiRevision> Generate() =>
        from isCurrent in Gen.Bool
        from number in Gen.Int[1, 100]
        select new ApiRevision
        {
            IsCurrent = isCurrent,
            Number = number
        };

    public bool Equals(ApiRevision? other) =>
        other is not null
        && Number == other.Number;

    public override int GetHashCode() =>
        Number.GetHashCode();
}

file sealed record ApiGroup
{
    public required ResourceName RootName { get; init; }
    public required Option<ResourceName> VersionSetName { get; init; }
    public required ImmutableHashSet<ApiRevision> Revisions { get; init; }
    public required string Description { get; init; }

    public IEnumerable<ApiModel> ToApiModels() =>
        from revision in Revisions
        select new ApiModel
        {
            Name = revision.IsCurrent ? RootName : ApiRevisionModule.Combine(RootName, revision.Number),
            RevisionNumber = revision.Number,
            Description = Description,
            VersionSetName = VersionSetName
        };

    public static Gen<ApiGroup> Generate(IEnumerable<ModelNode> predecessors) =>
        from rootName in Generator.ResourceName
        let versionSetName = predecessors.PickName<VersionSetResource>()
        from revisions in ApiRevision.Generate().HashSetOf(1, 5)
        from description in GenerateDescription()
        select new ApiGroup
        {
            RootName = rootName,
            VersionSetName = versionSetName,
            Revisions = [.. revisions.OrderBy(revision => revision.Number)
                                     .Select((revision, index) => revision with
                                     {
                                         Number = index == 0 ? 1 : revision.Number, // Ensure the first revision is always 1
                                         IsCurrent = index == 0 // The first revision is the current one
                                     })],
            Description = description
        };

    private static Gen<string> GenerateDescription() =>
        from lorem in Generator.Lorem
        select lorem.Paragraph();

    public static Gen<ApiGroup> GenerateUpdate(ApiGroup apiGroup) =>
        from revisions in from unchanged in Gen.Const(apiGroup.Revisions)
                          from @new in ApiRevision.Generate().HashSetOf(1, 5)
                          from selection in Generator.SubSetOf([.. unchanged, .. @new])
                          where selection.Count > 0
                          select selection
        from description in GenerateDescription()
        from currentRevision in Gen.OneOfConst([.. revisions.Select(revision => revision.Number)])
        select new ApiGroup
        {
            RootName = apiGroup.RootName,
            VersionSetName = apiGroup.VersionSetName,
            Revisions = [.. revisions.Select(revision => revision with
            {
                IsCurrent = revision.Number == currentRevision
            })],
            Description = description
        };

    public static ImmutableHashSet<(ApiGroup Group, ModelNodeSet Predecessors)> Parse(ResourceModels models)
    {
        var apis = models.Choose<ApiModel>();

        var set = from api in apis
                  let rootName = ApiRevisionModule.GetRootName(api.Model.Name)
                  group api by rootName into apiGroup
                  select (new ApiGroup
                  {
                      RootName = apiGroup.Key,
                      VersionSetName = apiGroup.Select(x => x.Model.VersionSetName).GetFirstOfUniform(),
                      Revisions = [.. from revision in apiGroup
                                      select new ApiRevision
                                      {
                                          IsCurrent = ApiRevisionModule.IsRootName(revision.Model.Name),
                                          Number = revision.Model.RevisionNumber
                                      } ],
                      Description = apiGroup.Select(x => x.Model.Description).GetFirstOfUniform()
                  }, apiGroup.Select(x => x.Predecessors).First());

        return [.. set];
    }

    public bool Equals(ApiGroup? other) =>
        other is not null
        && RootName == other.RootName;

    public override int GetHashCode() =>
        RootName.GetHashCode();
}

internal sealed record ApiModel : IResourceWithReferenceTestModel<ApiModel>
{
    public required ResourceName Name { get; init; }

    public required int RevisionNumber
    {
        get;
        init => field = value < 1 ? throw new InvalidOperationException("Revision must be greater than 0.") : value;
    }

    public required string Description { get; init; }

    public required Option<ResourceName> VersionSetName { get; init; }

    public static IResourceWithReference AssociatedResource { get; } = ApiResource.Instance;

    public static Gen<ModelNodeSet> GenerateNodes(ResourceModels baseline)
    {
        var existing = ApiGroup.Parse(baseline);

        var unchangedGenerator = Generator.SubSetOf([.. existing]);

        var updatedGenerator =
            from toUpdate in Generator.SubSetOf([.. existing])
            from updated in Generator.Traverse(toUpdate, ApiGroup.GenerateUpdate)
            select updated;

        var newGenerator = from predecessors in Generator.GeneratePredecessors<ApiModel>(baseline)
                                                         .IfNone(() => Gen.Const(ModelNodeSet.Empty))
                           from apiGroup in ApiGroup.Generate(predecessors)
                           select (apiGroup, predecessors);

        var newSetGenerator = newGenerator.HashSetOf();

        return from @new in newSetGenerator
               let deduplicatedNew = deduplicate(@new, [])
               from updated in updatedGenerator
               let deduplicatedUpdated = deduplicate(updated, deduplicatedNew)
               from unchanged in unchangedGenerator
               let deduplicatedUnchanged = deduplicate(unchanged, [.. deduplicatedUpdated, .. deduplicatedNew])
               let merged = deduplicatedNew.Union(deduplicatedUpdated).Union(deduplicatedUnchanged).ToImmutableArray()
               // Transform groups to API models
               let nodes = from all in merged
                           from model in all.Group.ToApiModels()
                           select ModelNode.From(model, all.Predecessors)
               select ModelNodeSet.From(nodes);

        ImmutableArray<(ApiGroup Group, ModelNodeSet Predecessors)> deduplicate(IEnumerable<(ApiGroup, ModelNodeSet)> groups, IEnumerable<(ApiGroup, ModelNodeSet)> existing)
        {
            var existingRootNames = existing.Select(group => group.Item1.RootName).ToImmutableHashSet();
            var existingVersionSetNames = existing.Choose(group => group.Item1.VersionSetName).ToImmutableHashSet();

            return [.. groups.DistinctBy(group => group.Item1.RootName)
                             // Each group must have a unique version set name, or no version set name at all
                             .DistinctBy(group => group.Item1.VersionSetName.Map(name => name.ToString())
                                                                            .IfNone(() => Guid.NewGuid().ToString()))
                             .Where(group => existingRootNames.Contains(group.Item1.RootName) is false)
                             .Where(group => group.Item1.VersionSetName.Map(versionSetName => existingVersionSetNames.Contains(versionSetName) is false)
                                                                       .IfNone(() => true))];
        }
    }

    public JsonObject SerializeDto(ModelNodeSet predecessors) =>
        JsonObjectModule.From(new ApiDto()
        {
            Properties = new ApiDto.ApiCreateOrUpdateProperties
            {
                Description = Description,
                IsCurrent = Name == ApiRevisionModule.GetRootName(Name),
                DisplayName = ApiRevisionModule.GetRootName(Name).ToString(),
                Protocols = ["http", "https"],
                Path = $"{ApiRevisionModule.GetRootName(Name)}",
                ApiRevision = RevisionNumber.ToString(),
                ApiVersionSetId = predecessors.Pick(node => from name in VersionSetName
                                                            where node.Model.AssociatedResource is VersionSetResource
                                                            select node.ToResourceId())
                                              .IfNoneNull()
            }
        }, AssociatedResource.SerializerOptions).IfErrorThrow();

    public bool MatchesDto(JsonObject json, Option<JsonObject> overrideJson)
    {
        var jsonDto = JsonNodeModule.To<ApiDto>(json, AssociatedResource.SerializerOptions)
                                    .IfErrorNull();

        var overrideDto = overrideJson.Bind(json => JsonNodeModule.To<ApiDto>(json, AssociatedResource.SerializerOptions)
                                                                  .ToOption())
                                      .IfNoneNull();

        var left = new
        {
            RevisionNumber = overrideDto?.Properties?.ApiRevision ?? RevisionNumber.ToString(),
            Description = overrideDto?.Properties?.Description ?? Description,
            VersionSetName = overrideDto?.Properties?.ApiVersionSetId?.Split('/')?.LastOrDefault() ?? VersionSetName.IfNoneNull()?.ToString()
        };

        var right = new
        {
            RevisionNumber = jsonDto?.Properties?.ApiRevision,
            Description = jsonDto?.Properties?.Description,
            VersionSetName = jsonDto?.Properties?.ApiVersionSetId?.Split('/')?.LastOrDefault()
        };

        return left.RevisionNumber.FuzzyEquals(right.RevisionNumber)
               && left.Description.FuzzyEquals(right.Description)
               && left.VersionSetName.FuzzyEquals(right.VersionSetName);
    }
}

file static class Extensions
{
    /// <summary>
    /// Ensures the source's elements are all the same, and returns that element.
    /// </summary>
    public static T GetFirstOfUniform<T>(this IEnumerable<T> source)
    {
        using var enumerator = source.GetEnumerator();
        if (enumerator.MoveNext() is false)
        {
            throw new InvalidOperationException("Sequence contains no elements.");
        }

        var element = enumerator.Current;
        var comparer = EqualityComparer<T>.Default;
        while (enumerator.MoveNext())
        {
            if (comparer.Equals(element, enumerator.Current) is false)
            {
                throw new InvalidOperationException($"Sequence contains at least two different elements: {element} and {enumerator.Current}.");
            }
        }

        return element;
    }
}