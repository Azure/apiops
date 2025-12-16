using common;
using CsCheck;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Nodes;

namespace integration.tests;

internal abstract record ApiType
{
#pragma warning disable CA1515 // Consider making public types internal
    public sealed record Http : ApiType
    {
        private Http() { }

        public static Http Instance { get; } = new Http();

        public override string ToString() => "http";
    }

    public sealed record Soap : ApiType
    {
        private Soap() { }

        public static Soap Instance { get; } = new Soap();

        public override string ToString() => "soap";
    }

    public sealed record WebSocket : ApiType
    {
        private WebSocket() { }

        public static WebSocket Instance { get; } = new WebSocket();

        public override string ToString() => "websocket";
    }

    public sealed record GraphQl : ApiType
    {
        private GraphQl() { }

        public static GraphQl Instance { get; } = new GraphQl();

        public override string ToString() => "graphql";
    }
#pragma warning restore CA1515 // Consider making public types internal

    public static Gen<ApiType> Generate() =>
        Gen.OneOfConst<ApiType>(Http.Instance,
                                Soap.Instance,
                                WebSocket.Instance,
                                GraphQl.Instance);
}

file sealed record ApiRevision
{
    public required int Number
    {
        get;
        init => field = value < 1 ? throw new InvalidOperationException("Revision must be greater than 0.") : value;
    }

    public required Option<string> Specification { get; init; }

    public static Gen<ApiRevision> Generate(ApiType type, ResourceName rootName, Option<string> descriptionOption, Option<Uri> serviceUriOption) =>
        from number in Gen.Int[1, 100]
        let revisionedName = ApiRevisionModule.Combine(rootName, number)
        let description = descriptionOption.IfNone(() => string.Empty)
        from specification in type switch
        {
            ApiType.Http => ApiSpecificationModule.GenerateOpenApi(revisionedName.ToString(), description).OptionOf(),
            ApiType.Soap => from specification in ApiSpecificationModule.GenerateWsdl(serviceUriOption.IfNone(() => throw new InvalidOperationException("Soap specification must have a service URI.")),
                                                                                      rootName.ToString())
                            select Option.Some(specification),
            ApiType.WebSocket => Gen.Const(Option<string>.None),
            ApiType.GraphQl => ApiSpecificationModule.GenerateGraphQl().OptionOf(),
            _ => throw new InvalidOperationException($"Unknown API type: {type}.")
        }
        select new ApiRevision
        {
            Number = number,
            Specification = specification
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
    public required ApiType Type { get; init; }
    public required Option<ResourceName> VersionSetName { get; init; }
    public required ApiRevision CurrentRevision { get; init; }
    public required ImmutableArray<ApiRevision> Revisions { get; init; }
    public required Option<string> Description { get; init; }
    public required Option<Uri> ServiceUri { get; init; }

    public IEnumerable<ApiModel> ToApiModels() =>
        from revision in Revisions
        select new ApiModel
        {
            Name = revision.Number == CurrentRevision.Number
                    ? RootName
                    : ApiRevisionModule.Combine(RootName, revision.Number),
            RevisionNumber = revision.Number,
            Description = Description,
            VersionSetName = VersionSetName,
            Type = Type,
            ServiceUrl = ServiceUri.Map(uri => uri.ToString()),
            Specification = revision.Specification
        };

    public static Gen<ApiGroup> Generate(IEnumerable<ModelNode> predecessors) =>
        from rootName in Generator.ResourceName
        from type in ApiType.Generate()
        from descriptionOption in GenerateDescription(type)
        from serviceUriOption in GenerateServiceUri(type)
        let versionSetName = predecessors.PickName<VersionSetResource>()
        from currentRevision in from revision in ApiRevision.Generate(type, rootName, descriptionOption, serviceUriOption)
                                select revision with { Number = 1 }
        from otherRevisions in from revisions in ApiRevision.Generate(type, rootName, descriptionOption, serviceUriOption)
                                                            .HashSetOf(0, 4)
                               where revisions.Contains(currentRevision) is false
                               select revisions
        select new ApiGroup
        {
            RootName = rootName,
            VersionSetName = versionSetName,
            Type = type,
            ServiceUri = serviceUriOption,
            CurrentRevision = currentRevision,
            Revisions = [currentRevision, .. otherRevisions],
            Description = descriptionOption
        };

    private static Gen<Option<string>> GenerateDescription(ApiType type) =>
        type switch
        {
            // TODO: Revisit description logic for Soap APIs.
            // Here's a complicating scenario:
            // 1. Create a SOAP API with a description.
            // 2. Pass a WSDL specification that does not contain a description.
            // 3. The description is removed.
            // Until we find a way to embed escription in WSDL, always set the description to None.
            ApiType.Soap => Gen.Const(Option<string>.None()),
            _ => from lorem in Generator.Lorem
                 let paragraph = lorem.Paragraph()
                 select Option.Some(paragraph)
        };

    private static Gen<Option<Uri>> GenerateServiceUri(ApiType type) =>
        type switch
        {
            ApiType.WebSocket => from uri in Generator.Uri
                                 let builder = new UriBuilder(uri)
                                 {
                                     Scheme = "wss",
                                     Port = -1
                                 }
                                 select Option.Some(builder.Uri),
            ApiType.Soap => from uri in Generator.Uri
                            select Option.Some(uri),
            ApiType.GraphQl => Generator.Uri.OptionOf(),
            _ => Generator.Uri.OptionOf()
        };

    public static Gen<ApiGroup> GenerateUpdate(ApiGroup apiGroup) =>
        from revisions in from unchanged in Gen.Const(apiGroup.Revisions) // Old set of revisions
                          from @new in ApiRevision.Generate(apiGroup.Type, apiGroup.RootName, apiGroup.Description, apiGroup.ServiceUri)
                                                  .HashSetOf(1, 5) // New set of revisions (may be empty)
                          from selection in Generator.SubSetOf([.. unchanged, .. @new]) // Randomly pick revisions from either set
                          where selection.Count > 0 // Ensure at least one revision was selected
                          select selection
        from description in GenerateDescription(apiGroup.Type)
        from currentRevision in Gen.OneOfConst([.. revisions])
        select new ApiGroup
        {
            RootName = apiGroup.RootName,
            VersionSetName = apiGroup.VersionSetName,
            Type = apiGroup.Type,
            ServiceUri = apiGroup.ServiceUri,
            CurrentRevision = currentRevision,
            Revisions = [.. revisions],
            Description = description
        };

    public static ImmutableHashSet<(ApiGroup Group, ModelNodeSet Predecessors)> Parse(ResourceModels models)
    {
        var apis = models.Choose<ApiModel>();

        var set = from api in apis
                  let rootName = ApiRevisionModule.GetRootName(api.Model.Name)
                  group api by rootName into apiGroup
                  let rootModel = apiGroup.Single(model => ApiRevisionModule.IsRootName(model.Model.Name)).Model
                  let revisions = apiGroup.Select(model => new ApiRevision
                  {
                      Number = model.Model.RevisionNumber,
                      Specification = model.Model.Specification
                  }).ToImmutableArray()
                  let currentRevision = revisions.Single(revision => revision.Number == rootModel.RevisionNumber)
                  select (new ApiGroup
                  {
                      RootName = apiGroup.Key,
                      VersionSetName = apiGroup.Select(x => x.Model.VersionSetName).GetFirstOfUniform(),
                      Type = apiGroup.Select(x => x.Model.Type).GetFirstOfUniform(),
                      ServiceUri = apiGroup.Select(x => x.Model.ServiceUrl)
                                           .GetFirstOfUniform()
                                           .Map(url => new Uri(url)),
                      CurrentRevision = currentRevision,
                      Revisions = revisions,
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

    public required Option<string> Description { get; init; }

    public required ApiType Type { get; init; }

    public required Option<string> ServiceUrl { get; init; }

    public required Option<string> Specification { get; init; }

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
                Description = Description.IfNoneNull(),
                IsCurrent = Name == ApiRevisionModule.GetRootName(Name),
                DisplayName = ApiRevisionModule.GetRootName(Name).ToString(),
                Protocols = Type switch
                {
                    ApiType.WebSocket => ["wss"],
                    _ => ["http", "https"],
                },
                Path = $"{ApiRevisionModule.GetRootName(Name)}",
                ApiRevision = RevisionNumber.ToString(),
                ApiVersionSetId = predecessors.Pick(node => from name in VersionSetName
                                                            where node.Model.AssociatedResource is VersionSetResource
                                                            select node.ToResourceId())
                                              .IfNoneNull(),
                ServiceUrl = ServiceUrl.IfNoneNull(),
                Type = Type is ApiType.WebSocket or ApiType.GraphQl
                        ? Type.ToString()
                        : null
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
            VersionSetName = overrideDto?.Properties?.ApiVersionSetId?.Split('/')?.LastOrDefault() ?? VersionSetName.IfNoneNull()?.ToString(),
            Type = overrideDto?.Properties?.Type ?? Type.ToString(),
            ServiceUrl = Type is ApiType.GraphQl or ApiType.WebSocket
                            ? overrideDto?.Properties?.ServiceUrl ?? ServiceUrl.IfNoneNull()
                            : null
        };

        var right = new
        {
            RevisionNumber = jsonDto?.Properties?.ApiRevision,
            Description = jsonDto?.Properties?.Description,
            VersionSetName = jsonDto?.Properties?.ApiVersionSetId?.Split('/')?.LastOrDefault(),
            Type = jsonDto?.Properties?.Type ?? "http",
            ServiceUrl = new string[] { "graphql", "websocket" }.Contains(jsonDto?.Properties?.Type, StringComparer.OrdinalIgnoreCase)
                            ? jsonDto?.Properties?.ServiceUrl
                            : null
        };

        return left.RevisionNumber.FuzzyEquals(right.RevisionNumber)
               && left.Description.FuzzyEquals(right.Description)
               && left.VersionSetName.FuzzyEquals(right.VersionSetName)
               && left.Type.FuzzyEquals(right.Type)
               && left.ServiceUrl.FuzzyEquals(right.ServiceUrl);
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