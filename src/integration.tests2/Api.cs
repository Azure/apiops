using common;
using common.tests;
using CsCheck;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;

namespace integration.tests;

internal enum ApiType
{
    OpenApi,
    Wadl,
    Wsdl,
    GraphQl,
    WebSocket
}

internal static class ApiSpecificationModule
{
    private sealed record HttpOperation
    {
        public required ResourceName Name { get; init; }
        public required string Method { get; init; }
        public required int StatusCode { get; init; }
        public required string Description { get; init; }
    }

    public static ImmutableHashSet<string> HttpMethods { get; } = [
        "GET", "POST", "PUT", "DELETE", "PATCH", "OPTIONS", "HEAD", "TRACE"
    ];

    public static Gen<string> GenerateOpenApi(ICollection<ResourceName> operationNames, string path, string displayName, string description) =>
        from operations in GenerateHttpOperations(operationNames)
        let operationText = operations.Length > 0
                            ? string.Join(Environment.NewLine,
                                        from operation in operations
                                        select $"""
    {operation.Method.ToLower()}:
      operationId: {operation.Name}
      responses:
        "{operation.StatusCode}":
          description: {operation.Description}
""")
                            : """
    {}
"""
        select $"""
openapi: 3.0.1
info:
  title: {displayName}
  description: {description}
  version: "1.0.0"
paths:
  /{path}:
{operationText}
""";

    private static Gen<ImmutableArray<HttpOperation>> GenerateHttpOperations(ICollection<ResourceName> operationNames)
    {
        // Ensure we don't have more operations than methods.
        if (operationNames.Count > HttpMethods.Count)
        {
            throw new ArgumentException($"There are more operation names ({operationNames.Count}) than available HTTP methods ({HttpMethods.Count}).", nameof(operationNames));
        }

        return
            from shuffledOperationNames in Gen.Shuffle(operationNames.ToArray())
            from shuffledOperationMethods in Gen.Shuffle(HttpMethods.ToArray())
            let operationMethods = shuffledOperationNames.Zip(shuffledOperationMethods)
            from operations in Generator.Traverse(operationMethods, tuple =>
                from statusCode in Gen.Enum<HttpStatusCode>()
                from description in Generator.AlphanumericWord
                select new HttpOperation
                {
                    Name = tuple.First,
                    Method = tuple.Second,
                    StatusCode = (int)statusCode,
                    Description = description
                })
            select operations;
    }

    public static Gen<string> GenerateWadl(ICollection<ResourceName> operationNames, string displayName, string description, Uri serviceUrl) =>
        from operations in GenerateHttpOperations(operationNames)
        let operationText = string.Join(Environment.NewLine,
                                        from operation in operations
                                        select $"""
		<resource path="/{operation.Name}">
			<method id="{operation.Name}" name="{operation.Method}">
				<request>
				</request>
				<response status="{operation.StatusCode}">
					<representation mediaType="text/xml" />
				</response>
			</method>
		</resource>
""")
        select $"""
<?xml version="1.0" encoding="utf-8"?>
<application
	xmlns="http://wadl.dev.java.net/2009/02"
	xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
             xsi:schemaLocation="http://wadl.dev.java.net/2009/02 http://www.w3.org/Submission/wadl/wadl.xsd">
    <doc title="{displayName}">{description}</doc>
	<resources base="{serviceUrl}">
{operationText}
	</resources>
</application>
""";

    public static string GetWsdl(IEnumerable<ResourceName> operationNames, ResourceName apiName, string displayName, Uri serviceUrl)
    {
        var messageText = string.Join(Environment.NewLine,
                                      from operationName in operationNames
                                      select $"""
  <message name="{operationName}Request" />
  <message name="{operationName}Response" />
""");

        var portTypeOperationText = string.Join(Environment.NewLine,
                                               from operationName in operationNames
                                               select $"""
    <operation name="{operationName}">
      <input message="tns:{operationName}Request" />
      <output message="tns:{operationName}Response" />
    </operation>
""");
        var bindingOperationText = string.Join(Environment.NewLine,
                                               from operationName in operationNames
                                               select $"""
    <operation name="{operationName}">
      <soap:operation soapAction="urn:{apiName}/{operationName}" />
      <input>
        <soap:body use="literal" />
      </input>
      <output>
        <soap:body use="literal" />
      </output>
    </operation>
""");

        return $"""
<?xml version="1.0" encoding="UTF-8"?>
<definitions xmlns="http://schemas.xmlsoap.org/wsdl/"
             xmlns:soap="http://schemas.xmlsoap.org/wsdl/soap/"
             xmlns:tns="urn:{apiName}"
             targetNamespace="urn:{apiName}">
  <types />
{messageText}
  <portType name="{apiName}PortType">
{portTypeOperationText}
  </portType>
  <binding name="{apiName}Binding" type="tns:{apiName}PortType">
    <soap:binding style="document" transport="http://schemas.xmlsoap.org/soap/http" />
{bindingOperationText}
  </binding>
  <service name="{displayName}">
    <port name="{apiName}Port" binding="tns:{apiName}Binding">
      <soap:address location="{serviceUrl}" />
    </port>
  </service>
</definitions>
""";
    }

    public static string GetGraphQl(ResourceName apiName)
    {
        var sanitizedName = new string([.. apiName.ToString() switch
            {
                [var first, .. var rest] when char.IsLetter(first) => ImmutableArray.Create([first, .. rest.Where(char.IsLetterOrDigit)]),
                [var first, .. var rest] => [.. rest.Where(char.IsLetterOrDigit)],
                _ => []
            }]);

        return $$"""
                schema {
                query: Query
                }

                type Query {
                {{sanitizedName}}: String
                }
                """;
    }
}

internal sealed record ApiModel : ITestModel<ApiModel>
{
    public required ResourceKey Key { get; init; }

    public ResourceName RootName => ApiRevisionModule.GetRootName(Key.Name);

    public required int RevisionNumber { get; init; }

    public required ApiType Type { get; init; }

    public required string DisplayName { get; init; }

    public required string Description { get; init; }

    public required string Path { get; init; }

    public required bool SubscriptionRequired { get; init; }

    public required Option<ResourceName> VersionSetName { get; init; }

    public required Option<Uri> ServiceUrl { get; init; }

    public required Option<ImmutableHashSet<ResourceName>> OperationNames { get; init; }

    public required Option<string> Specification { get; init; }

    public JsonObject ToDto()
    {
        var propertiesJson = new JsonObject
        {
            ["displayName"] = DisplayName,
            ["description"] = Description,
            ["path"] = Path,
            ["subscriptionRequired"] = SubscriptionRequired,
            ["apiRevision"] = RevisionNumber.ToString(),
            ["apiVersionSetId"] = VersionSetName.Map(name => $"/apiVersionSets/{name}")
                                                .IfNoneNull(),
            ["protocols"] = Type switch
            {
                ApiType.WebSocket => new JsonArray("wss"),
                _ => ["https"]
            }
        };

        // Set type
        var typeOption = Type switch
        {
            ApiType.GraphQl => Option.Some("graphql"),
            ApiType.WebSocket => Option.Some("websocket"),
            _ => Option.None
        };
        typeOption.Iter(type => propertiesJson["type"] = type);

        // Set serviceUrl
        ServiceUrl.Iter(uri => propertiesJson["serviceUrl"] = uri.ToString());

        // Set version set
        VersionSetName.Iter(name => propertiesJson["apiVersionSetId"] = $"/apiVersionSets/{name}");

        return new()
        {
            ["properties"] = propertiesJson
        };
    }

    public Result<Unit> ValidateDto(JsonObject dto)
    {
        return from _ in validateDisplayName()
               from __ in validateDescription()
               from ___ in validatePath()
               from ____ in validateRevision()
               from _____ in validateType()
               from ______ in validateVersionSet()
               from _______ in validateSubscriptionRequired()
               select Unit.Instance;

        Result<Unit> validateDisplayName() =>
            from properties in dto.GetJsonObjectProperty("properties")
            from displayName in properties.GetStringProperty("displayName")
            from unit in displayName == DisplayName
                        ? Result.Success(Unit.Instance)
                        : Error.From($"Resource '{Key}' has displayName '{displayName}' instead of '{DisplayName}'.")
            select unit;

        Result<Unit> validateDescription() =>
            from properties in dto.GetJsonObjectProperty("properties")
            from description in properties.GetStringProperty("description")
            from unit in description == Description
                        ? Result.Success(Unit.Instance)
                        : Error.From($"Resource '{Key}' has description '{description}' instead of '{Description}'.")
            select unit;

        Result<Unit> validatePath() =>
            from properties in dto.GetJsonObjectProperty("properties")
            from path in properties.GetStringProperty("path")
            from unit in path == Path
                        ? Result.Success(Unit.Instance)
                        : Error.From($"Resource '{Key}' has path '{path}' instead of '{Path}'.")
            select unit;

        Result<Unit> validateRevision() =>
            from properties in dto.GetJsonObjectProperty("properties")
            from apiRevision in properties.GetStringProperty("apiRevision")
            from unit in apiRevision == RevisionNumber.ToString()
                        ? Result.Success(Unit.Instance)
                        : Error.From($"Resource '{Key}' has apiRevision '{apiRevision}' instead of '{RevisionNumber}'.")
            select unit;

        Result<Unit> validateType() =>
            from properties in dto.GetJsonObjectProperty("properties")
            let actualType = properties.GetStringProperty("type")
                                       .ToOption()
                                       .IfNone(() => "http")
            let expectedType = Type switch
            {
                ApiType.GraphQl => "graphql",
                ApiType.WebSocket => "websocket",
                ApiType.Wsdl => "soap",
                _ => "http"
            }
            from unit in actualType.Equals(expectedType, StringComparison.OrdinalIgnoreCase)
                        ? Result.Success(Unit.Instance)
                        : Error.From($"Resource '{Key}' has type '{actualType}' instead of '{expectedType}'.")
            select unit;

        Result<Unit> validateVersionSet() =>
            from properties in dto.GetJsonObjectProperty("properties")
            let versionSetIdOption = properties.GetStringProperty("apiVersionSetId")
                                               .ToOption()
            from unit in (VersionSetName.IfNoneNull(), versionSetIdOption.IfNoneNull()) switch
            {
                (null, null) => Result.Success(Unit.Instance),
                (null, var value) => Error.From($"Resource '{Key}' has apiVersionSetId '{value}', but it should be missing."),
                (var value, null) => Error.From($"Resource '{Key}' is missing apiVersionSetId, but it should be '{value}'."),
                (var expected, var actual) => actual.EndsWith($"/apiVersionSets/{expected}", StringComparison.OrdinalIgnoreCase)
                                                ? Result.Success(Unit.Instance)
                                                : Error.From($"Resource '{Key}' has apiVersionSetId '{actual}', but it should end with '/apiVersionSets/{expected}'.")
            }
            select unit;

        Result<Unit> validateSubscriptionRequired() =>
            from properties in dto.GetJsonObjectProperty("properties")
            from subscriptionRequired in properties.GetBoolProperty("subscriptionRequired")
            from unit in subscriptionRequired == SubscriptionRequired
                ? Result.Success(Unit.Instance)
                : Error.From($"Resource '{Key}' has subscriptionRequired '{subscriptionRequired}' instead of '{SubscriptionRequired}'.")
            select unit;
    }

    public static Gen<ImmutableHashSet<ApiModel>> GenerateSet(IEnumerable<ITestModel> models) =>
        from rootName in Generator.ResourceName
        from displayName in CommonModule.GenerateDisplayName(rootName)
        from description in CommonModule.GenerateDescription(rootName)
        let path = rootName.ToString()
        from apiType in Gen.Enum<ApiType>()
        where apiType != ApiType.WebSocket // Skip websocket generation for now, unsupported in certain APIM SKUs
        from serviceUrl in apiType switch
        {
            ApiType.Wadl or ApiType.Wsdl or ApiType.WebSocket => Generator.AbsoluteUri.Select(Option.Some),
            _ => Generator.AbsoluteUri.OptionOf()
        }
        from versionSetName in Gen.OneOfConst([.. models.OfType<VersionSetModel>()
                                                        .Select(model => model.Key.Name)
                                                        .Select(Option.Some)
                                                        .Append(Option.None)])
        from operationNames in GenerateOperationNames(apiType)
        from specification in GenerateSpecification(apiType, rootName, operationNames, serviceUrl, path, displayName, description)
        from subscriptionRequired in Gen.Bool
        let currentRevision = new ApiModel
        {
            Description = description,
            DisplayName = displayName,
            Key = ResourceKey.From(ApiResource.Instance, rootName),
            OperationNames = operationNames,
            Path = path,
            RevisionNumber = 1,
            ServiceUrl = serviceUrl,
            Specification = specification,
            SubscriptionRequired = subscriptionRequired,
            Type = apiType,
            VersionSetName = versionSetName
        }
        from otherRevisionNumbers in Gen.Int[2, 100].HashSetOf(0, 3)
        from otherRevisions in Generator.Traverse(otherRevisionNumbers, revisionNumber =>
            from operationNames in GenerateOperationNames(apiType)
            let name = ApiRevisionModule.Combine(rootName, revisionNumber)
            from specification in GenerateSpecification(apiType, name, operationNames, serviceUrl, path, displayName, description)
            select currentRevision with
            {
                Key = currentRevision.Key with
                {
                    Name = name
                },
                RevisionNumber = revisionNumber,
                OperationNames = operationNames,
                Specification = specification
            })
        select ToSet([currentRevision, .. otherRevisions]);

    private static Gen<Option<ImmutableHashSet<ResourceName>>> GenerateOperationNames(ApiType apiType) =>
        apiType switch
        {
            ApiType.OpenApi => Generator.ResourceName
                                        .HashSetOf(0, ApiSpecificationModule.HttpMethods.Count)
                                        .Select(Option.Some),
            ApiType.Wadl or ApiType.Wsdl => Generator.ResourceName
                                                     .HashSetOf(1, ApiSpecificationModule.HttpMethods.Count)
                                                     .Select(Option.Some),
            _ => Gen.Const(Option<ImmutableHashSet<ResourceName>>.None())
        };

    private static Gen<Option<string>> GenerateSpecification(ApiType apiType, ResourceName apiName, Option<ImmutableHashSet<ResourceName>> operationNamesOption, Option<Uri> serviceUriOption, string path, string displayName, string description)
    {
        var rootApiName = ApiRevisionModule.GetRootName(apiName);

        if (apiType is ApiType.OpenApi)
        {
            var operationNames = operationNamesOption.IfNone(() => throw new InvalidOperationException("Operation names cannot be None for API type OpenApi."));

            return from specification in ApiSpecificationModule.GenerateOpenApi(operationNames, path, displayName, description)
                   select Option.Some(specification);
        }
        else if (apiType is ApiType.Wadl)
        {
            var operationNames = operationNamesOption.IfNone(() => throw new InvalidOperationException("Operation names cannot be None for API type Wadl."));
            var serviceUrl = serviceUriOption.IfNone(() => throw new InvalidOperationException("Service URL cannot be None for API type Wadl."));

            return from specification in ApiSpecificationModule.GenerateWadl(operationNames, displayName, description, serviceUrl)
                   select Option.Some(specification);
        }
        else if (apiType is ApiType.Wsdl)
        {
            // We cannot reliably publish specifications for non-current API revisions.
            if (apiName != rootApiName)
            {
                return Gen.Const(Option<string>.None());
            }

            var operationNames = operationNamesOption.IfNone(() => throw new InvalidOperationException("Operation names cannot be None for API type Wsdl."));
            var serviceUrl = serviceUriOption.IfNone(() => throw new InvalidOperationException("Service URL cannot be None for API type Wsdl."));
            var specification = ApiSpecificationModule.GetWsdl(operationNames, rootApiName, displayName, serviceUrl);

            return Gen.Const(Option.Some(specification));
        }
        else if (apiType is ApiType.GraphQl)
        {
            var specification = ApiSpecificationModule.GetGraphQl(rootApiName);

            return Gen.Const(Option.Some(specification));
        }
        else
        {
            return Gen.Const(Option<string>.None());
        }
    }

    private static ImmutableHashSet<ApiModel> ToSet(IEnumerable<ApiModel> models)
    {
        var rootGroups = models.GroupBy(model => model.RootName);

        // Deduplicate group display names
        rootGroups = rootGroups.DistinctBy(group => group.First().DisplayName, StringComparer.OrdinalIgnoreCase);

        // Deduplicate version set names
        rootGroups = rootGroups.DistinctBy(group => group.First()
                                                         .VersionSetName
                                                         .Map(name => name.ToString())
                                                         .IfNone(() => Guid.NewGuid().ToString()));

        return [.. rootGroups.SelectMany(group => group)];
    }

    public static Gen<ImmutableHashSet<ApiModel>> GenerateUpdates(IEnumerable<ApiModel> apiModels, IEnumerable<ITestModel> allModels) =>
        GenerateUpdates(apiModels, allowNewCurrentRevision: false);

    private static Gen<ImmutableHashSet<ApiModel>> GenerateUpdates(IEnumerable<ApiModel> models, bool allowNewCurrentRevision)
    {
        var rootGroups = models.GroupBy(model => model.RootName);

        return
            from updatedRootGroups in Generator.Traverse(rootGroups, @group =>
            {
                var firstModel = @group.First();

                return from newSubscriptionRequired in Gen.Const(!firstModel.SubscriptionRequired)
                       from newCurrentRevisionNumber in
                        allowNewCurrentRevision
                        ? Gen.OneOfConst([.. from model in @group
                                             select model.RevisionNumber])
                        : Gen.Const(@group.Single(model => model.Key.Name == firstModel.RootName).RevisionNumber)
                       select @group.Select(model => model with
                       {
                           SubscriptionRequired = newSubscriptionRequired,
                           // If a model is now the current revision, update its name to the root name
                           Key = model.Key with
                           {
                               Name = model.RevisionNumber == newCurrentRevisionNumber
                                        ? model.RootName
                                        : ApiRevisionModule.Combine(model.RootName, model.RevisionNumber)
                           }
                       });
            })
            from updatedModels in Generator.Traverse(updatedRootGroups.SelectMany(@group => @group),
                                                     model => from names in GenerateOperationNames(model.Type)
                                                              from specification in GenerateSpecification(model.Type, model.Key.Name, names, model.ServiceUrl, model.Path, model.DisplayName, model.Description)
                                                              select model with
                                                              {
                                                                  OperationNames = names,
                                                                  Specification = specification
                                                              })
            let updatedSet = ToSet([.. updatedModels])
            where updatedSet.Count == updatedModels.Length
            select updatedSet;
    }

    public static Gen<ImmutableHashSet<ApiModel>> GenerateNextState(IEnumerable<ITestModel> previousModels, IEnumerable<ITestModel> accumulatedNextModels)
    {
        var currentModels = previousModels.OfType<ApiModel>()
                                          .ToImmutableArray();

        var currentRootGroups = currentModels.GroupBy(model => model.RootName);

        return from shuffled in Gen.Shuffle(currentRootGroups.ToArray())
               from keptCount in Gen.Int[0, shuffled.Length]
               let kept = shuffled.Take(keptCount).ToImmutableArray()
               from unchangedCount in Gen.Int[0, kept.Length]
               let unchanged = kept.Take(unchangedCount)
                                   .SelectMany(@group => @group)
               from changed in GenerateUpdates(kept.Skip(unchangedCount)
                                                   .SelectMany(@group => @group),
                                               allowNewCurrentRevision: true)
               from added in GenerateSet(accumulatedNextModels)
               let normalized = ensureVersionSetContinuity([.. unchanged, .. changed, .. added])
               select ToSet(normalized);

        // Make sure that a version set doesn't have all brand-new APIs.
        // Otherwise, we can get in a race situation like this:
        // - Set A has APIs 1 and 2
        // - In next state, Set A has APIs 3 and 4. 1 and 2 are deleted.
        // - When the publisher runs
        //   - It deletes 1 and 2
        //   - APIM automatically deletes Set A since it has no APIs
        //   - Publisher tries to put APIs 3 and 4. It fails because Set A doesn't exist.
        IEnumerable<ApiModel> ensureVersionSetContinuity(ICollection<ApiModel> next)
        {
            var currentVersionSetApis = getVersionSetApis(currentModels);
            var nextVersionSetApis = getVersionSetApis(next);

            // Get next version sets where all APIs have changed.
            var nonContinuousVersionSets = nextVersionSetApis.Choose(kvp =>
                                                              {
                                                                  var (nextVersionSetName, nextApis) = kvp;

                                                                  return currentVersionSetApis.TryGetValue(nextVersionSetName, out var currentApis)
                                                                         && currentApis.Intersect(nextApis).IsEmpty
                                                                          ? Option.Some(nextVersionSetName)
                                                                          : Option.None;
                                                              })
                                                             .ToImmutableHashSet();

            // Remove non-continuous version set references
            return next.Select(model => model.VersionSetName
                                             .Where(nonContinuousVersionSets.Contains)
                                             .Match(name => model with { VersionSetName = Option.None },
                                                    () => model));
        }

        ImmutableDictionary<ResourceName, ImmutableHashSet<ResourceName>> getVersionSetApis(IEnumerable<ApiModel> models) =>
            models.Choose(apiModel => from versionSetName in apiModel.VersionSetName
                                      select (versionSetName, apiModel.RootName))
                  .GroupBy(tuple => tuple.versionSetName, tuple => tuple.RootName)
                  .ToImmutableDictionary(group => group.Key, group => group.ToImmutableHashSet());
    }
}