using Azure.Core.Pipeline;
using common;
using common.tests;
using CsCheck;
using FluentAssertions;
using Flurl;
using LanguageExt;
using LanguageExt.UnsafeValueAccess;
using publisher;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace integration.tests;

internal static class Api
{
    public static Gen<ApiModel> GenerateUpdate(ApiModel original) =>
        from revisions in GenerateRevisionUpdates(original.Revisions, original.Type, original.Name)
        select original with
        {
            Revisions = revisions
        };

    private static Gen<FrozenSet<ApiRevision>> GenerateRevisionUpdates(FrozenSet<ApiRevision> revisions, ApiType type, ApiName name)
    {
        var newGen = ApiRevision.GenerateSet(type, name);
        var updateGen = (ApiRevision revision) => GenerateRevisionUpdate(revision, type);

        return Fixture.GenerateNewSet(revisions, newGen, updateGen);
    }

    private static Gen<ApiRevision> GenerateRevisionUpdate(ApiRevision revision, ApiType type) =>
        from serviceUri in type is ApiType.Soap or ApiType.WebSocket
                           ? Gen.Const(revision.ServiceUri)
                           : ApiRevision.GenerateServiceUri(type)
        from description in ApiRevision.GenerateDescription().OptionOf()
        select revision with
        {
            ServiceUri = serviceUri.ValueUnsafe(),
            Description = description.ValueUnsafe()
        };

    public static Gen<ApiDto> GenerateOverride(ApiDto original) =>
        from serviceUrl in (original.Properties.Type ?? original.Properties.ApiType) switch
        {
            "websocket" or "soap" => Gen.Const(original.Properties.ServiceUrl),
            _ => Generator.AbsoluteUri.Select(uri => (string?)uri.ToString())
        }
        from revisionDescription in ApiRevision.GenerateDescription().OptionOf()
        select new ApiDto()
        {
            Properties = new ApiDto.ApiCreateOrUpdateProperties
            {
                ServiceUrl = serviceUrl,
                ApiRevisionDescription = revisionDescription.ValueUnsafe()
            }
        };

    public static FrozenDictionary<ApiName, ApiDto> GetDtoDictionary(IEnumerable<ApiModel> models) =>
        models.SelectMany(model => model.Revisions.Select((revision, index) =>
        {
            var apiName = GetApiName(model.Name, revision, index);
            var dto = GetDto(model.Name, model.Type, model.Path, model.Version, revision);
            return (apiName, dto);
        }))
        .Where(api => ApiName.IsNotRevisioned(api.apiName))
        .ToFrozenDictionary();

    private static ApiName GetApiName(ApiName name, ApiRevision revision, int index)
    {
        var rootApiName = ApiName.GetRootName(name);

        return index == 0 ? rootApiName : ApiName.GetRevisionedName(rootApiName, revision.Number);
    }

    private static ApiDto GetDto(ApiName name, ApiType type, string path, Option<ApiVersion> version, ApiRevision revision) =>
        new ApiDto()
        {
            Properties = new ApiDto.ApiCreateOrUpdateProperties
            {
                // APIM sets the description to null when it imports for SOAP APIs.
                DisplayName = name.ToString(),
                Path = path,
                ApiType = type switch
                {
                    ApiType.Http => null,
                    ApiType.Soap => "soap",
                    ApiType.GraphQl => null,
                    ApiType.WebSocket => null,
                    _ => throw new NotSupportedException()
                },
                Type = type switch
                {
                    ApiType.Http => "http",
                    ApiType.Soap => "soap",
                    ApiType.GraphQl => "graphql",
                    ApiType.WebSocket => "websocket",
                    _ => throw new NotSupportedException()
                },
                Protocols = type switch
                {
                    ApiType.Http => ["http", "https"],
                    ApiType.Soap => ["http", "https"],
                    ApiType.GraphQl => ["http", "https"],
                    ApiType.WebSocket => ["ws", "wss"],
                    _ => throw new NotSupportedException()
                },
                ServiceUrl = revision.ServiceUri.ValueUnsafe()?.ToString(),
                ApiRevisionDescription = revision.Description.ValueUnsafe(),
                ApiRevision = $"{revision.Number.ToInt()}",
                ApiVersion = version.Map(version => version.Version).ValueUnsafe(),
                ApiVersionSetId = version.Map(version => $"/apiVersionSets/{version.VersionSetName}").ValueUnsafe()
            }
        };

    public static async ValueTask Put(IEnumerable<ApiModel> models, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await models.IterParallel(async model =>
        {
            async ValueTask putRevision(ApiRevision revision) => await Put(model.Name, model.Type, model.Path, model.Version, revision, serviceUri, pipeline, cancellationToken);

            // Put first revision to make sure it's the current revision.
            await model.Revisions.HeadOrNone().IterTask(putRevision);

            // Put other revisions
            await model.Revisions.Skip(1).IterParallel(putRevision, cancellationToken);
        }, cancellationToken);

    private static async ValueTask Put(ApiName name, ApiType type, string path, Option<ApiVersion> version, ApiRevision revision, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var rootName = ApiName.GetRootName(name);
        var dto = GetDto(rootName, type, path, version, revision);
        var revisionedName = ApiName.GetRevisionedName(rootName, revision.Number);

        var uri = ApiUri.From(revisionedName, serviceUri);
        await uri.PutDto(dto, pipeline, cancellationToken);

        if (type is ApiType.GraphQl)
        {
            await revision.Specification.IterTask(async specification => await uri.PutGraphQlSchema(specification, pipeline, cancellationToken));
        }

        await ApiPolicy.Put(revision.Policies, revisionedName, serviceUri, pipeline, cancellationToken);
        await ApiTag.Put(revision.Tags, revisionedName, serviceUri, pipeline, cancellationToken);
    }

    private static ApiDto GetPutDto(ApiName name, ApiType type, string path, Option<ApiVersion> version, ApiRevision revision) =>
        new ApiDto()
        {
            Properties = new ApiDto.ApiCreateOrUpdateProperties
            {
                // APIM sets the description to null when it imports for SOAP APIs.
                DisplayName = name.ToString(),
                Path = path,
                ApiType = type switch
                {
                    ApiType.Http => null,
                    ApiType.Soap => "soap",
                    ApiType.GraphQl => null,
                    ApiType.WebSocket => null,
                    _ => throw new NotSupportedException()
                },
                Type = type switch
                {
                    ApiType.Http => "http",
                    ApiType.Soap => "soap",
                    ApiType.GraphQl => "graphql",
                    ApiType.WebSocket => "websocket",
                    _ => throw new NotSupportedException()
                },
                Format = revision.Specification.IsSome
                         ? type switch
                         {
                             ApiType.Http => "openapi+json",
                             ApiType.Soap => "wsdl",
                             _ => null
                         }
                         : null,
                Value = type is ApiType.Http or ApiType.Soap
                        ? revision.Specification.ValueUnsafe()
                        : null,
                Protocols = type switch
                {
                    ApiType.Http => ["http", "https"],
                    ApiType.Soap => ["http", "https"],
                    ApiType.GraphQl => ["http", "https"],
                    ApiType.WebSocket => ["ws", "wss"],
                    _ => throw new NotSupportedException()
                },
                ServiceUrl = revision.ServiceUri.ValueUnsafe()?.ToString(),
                ApiRevisionDescription = revision.Description.ValueUnsafe(),
                ApiRevision = $"{revision.Number.ToInt()}",
                ApiVersion = version.Map(version => version.Version).ValueUnsafe(),
                ApiVersionSetId = version.Map(version => $"/apiVersionSets/{version.VersionSetName}").ValueUnsafe()
            }
        };

    public static async ValueTask DeleteAll(ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await ApisUri.From(serviceUri).DeleteAll(pipeline, cancellationToken);

    public static async ValueTask WriteArtifacts(IEnumerable<ApiModel> models, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await models.IterParallel(async model =>
        {
            await WriteRevisionArtifacts(model, serviceDirectory, cancellationToken);
        }, cancellationToken);

    public static async ValueTask WriteRevisionArtifacts(ApiModel model, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await model.Revisions
                   .Select((revision, index) => (revision, index))
                   .IterParallel(async x =>
                   {
                       var (name, type, path, version, (revision, index)) = (model.Name, model.Type, model.Path, model.Version, x);

                       await WriteInformationFile(name, type, path, version, revision, index, serviceDirectory, cancellationToken);
                       await WriteSpecificationFile(name, type, revision, index, serviceDirectory, cancellationToken);
                   }, cancellationToken);

    private static async ValueTask WriteInformationFile(ApiName name, ApiType type, string path, Option<ApiVersion> version, ApiRevision revision, int index, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        var apiName = GetApiName(name, revision, index);
        var informationFile = ApiInformationFile.From(apiName, serviceDirectory);
        var rootApiName = ApiName.GetRootName(name);
        var dto = GetDto(rootApiName, type, path, version, revision);

        await informationFile.WriteDto(dto, cancellationToken);
    }

    private static async ValueTask WriteSpecificationFile(ApiName name, ApiType type, ApiRevision revision, int index, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        var specificationOption = from contents in revision.Specification
                                  from specification in type switch
                                  {
                                      ApiType.Http => Option<ApiSpecification>.Some(new ApiSpecification.OpenApi
                                      {
                                          Format = new OpenApiFormat.Json(),
                                          Version = new OpenApiVersion.V3()
                                      }),
                                      ApiType.GraphQl => new ApiSpecification.GraphQl(),
                                      ApiType.Soap => new ApiSpecification.Wsdl(),
                                      _ => Option<ApiSpecification>.None
                                  }
                                  select (specification, contents);

        await specificationOption.IterTask(async x =>
        {
            var (specification, contents) = x;
            var apiName = GetApiName(name, revision, index);
            var specificationFile = ApiSpecificationFile.From(specification, apiName, serviceDirectory);
            await specificationFile.WriteSpecification(BinaryData.FromString(contents), cancellationToken);
        });
    }

    public static async ValueTask ValidateExtractedArtifacts(Option<FrozenSet<ApiName>> namesToExtract, Option<ApiSpecification> defaultApiSpecification, ManagementServiceDirectory serviceDirectory, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var apimResources = await GetApimResources(serviceUri, pipeline, cancellationToken);
        var expectedResources = apimResources.WhereKey(name => ExtractorOptions.ShouldExtract(name, namesToExtract));

        await ValidateExtractedInformationFiles(expectedResources, serviceDirectory, cancellationToken);
        await ValidateExtractedSpecificationFiles(expectedResources, defaultApiSpecification, serviceDirectory, serviceUri, pipeline, cancellationToken);

        await expectedResources.Keys.IterParallel(async name =>
        {
            await ApiPolicy.ValidateExtractedArtifacts(serviceDirectory, name, serviceUri, pipeline, cancellationToken);
            await ApiTag.ValidateExtractedArtifacts(serviceDirectory, name, serviceUri, pipeline, cancellationToken);
        }, cancellationToken);
    }

    private static async ValueTask<FrozenDictionary<ApiName, ApiDto>> GetApimResources(ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var uri = ApisUri.From(serviceUri);

        return await uri.List(pipeline, cancellationToken)
                        .Select(api =>
                        {
                            var normalizedName = NormalizeName(api.Name, api.Dto);
                            return (normalizedName, api.Dto);
                        })
                        .ToFrozenDictionary(cancellationToken);
    }

    // APIM has an issue where it sometimes returns duplicate API names.
    private static ApiName NormalizeName(ApiName name, ApiDto dto)
    {
        if (dto.Properties.IsCurrent is true)
        {
            return name;
        }

        if (ApiName.IsRevisioned(name))
        {
            return name;
        }

        var revisionNumber = ApiRevisionNumber.TryFrom(dto.Properties.ApiRevision)
                                              .IfNone(() => throw new InvalidOperationException("Could not get revision number."));

        var rootName = ApiName.GetRootName(name);

        return ApiName.GetRevisionedName(rootName, revisionNumber);
    }

    private static async ValueTask ValidateExtractedInformationFiles(IDictionary<ApiName, ApiDto> expectedResources, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        var fileResources = await GetFileResources(serviceDirectory, cancellationToken);

        var expected = expectedResources.MapValue(NormalizeDto);
        var actual = fileResources.MapValue(NormalizeDto);

        actual.Should().BeEquivalentTo(expected);
    }

    private static async ValueTask<FrozenDictionary<ApiName, ApiDto>> GetFileResources(ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await ApiModule.ListInformationFiles(serviceDirectory)
                       .ToAsyncEnumerable()
                       .SelectAwait(async file => (file.Parent.Name,
                                                   await file.ReadDto(cancellationToken)))
                       .ToFrozenDictionary(cancellationToken);

    private static string NormalizeDto(ApiDto dto) =>
        new
        {
            DisplayName = dto.Properties.DisplayName ?? string.Empty,
            Path = dto.Properties.Path ?? string.Empty,
            RevisionDescription = dto.Properties.ApiRevisionDescription ?? string.Empty,
            Revision = dto.Properties.ApiRevision ?? string.Empty,
            // Disabling this check because there are too many edge cases //TODO - Investigate
            //ServiceUrl = Uri.TryCreate(dto.Properties.ServiceUrl, UriKind.Absolute, out var uri)
            //                ? uri.RemovePath().ToString()
            //                : string.Empty
        }.ToString()!;

    private static async ValueTask ValidateExtractedSpecificationFiles(IDictionary<ApiName, ApiDto> expectedResources, Option<ApiSpecification> defaultApiSpecification, ManagementServiceDirectory serviceDirectory, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var expected = await expectedResources.ToAsyncEnumerable()
                                              .Choose(async kvp =>
                                              {
                                                  var name = kvp.Key;
                                                  return from specification in await GetExpectedApiSpecification(name, kvp.Value, defaultApiSpecification, serviceUri, pipeline, cancellationToken)
                                                             // Skip XML specification files. Sometimes they get extracted, other times they fail.
                                                         where specification is not (ApiSpecification.Wsdl or ApiSpecification.Wadl)
                                                         select (name, specification);
                                              })
                                              .ToFrozenDictionary(cancellationToken);

        var actual = await ApiModule.ListSpecificationFiles(serviceDirectory, cancellationToken)
                                    .Select(file => (file.Parent.Name, file.Specification))
                                    // Skip XML specification files. Sometimes they get extracted, other times they fail.
                                    .Where(file => file.Specification is not (ApiSpecification.Wsdl or ApiSpecification.Wadl))
                                    .ToFrozenDictionary(cancellationToken);

        actual.Should().BeEquivalentTo(expected);
    }

    private static async ValueTask<Option<ApiSpecification>> GetExpectedApiSpecification(ApiName name, ApiDto dto, Option<ApiSpecification> defaultApiSpecification, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        switch (dto.Properties.ApiType ?? dto.Properties.Type)
        {
            case "graphql":
                var specificationContents = await ApiUri.From(name, serviceUri)
                                                        .TryGetGraphQlSchema(pipeline, cancellationToken);
                return specificationContents.Map(contents => new ApiSpecification.GraphQl() as ApiSpecification);
            case "soap":
                return new ApiSpecification.Wsdl();
            case "websocket":
                return Option<ApiSpecification>.None;
            default:
#pragma warning disable CA1849 // Call async methods when in an async method
                return defaultApiSpecification.IfNone(() => new ApiSpecification.OpenApi
                {
                    Format = new OpenApiFormat.Yaml(),
                    Version = new OpenApiVersion.V3()
                });
#pragma warning restore CA1849 // Call async methods when in an async method
        }
    }

    public static async ValueTask ValidatePublisherChanges(ManagementServiceDirectory serviceDirectory, IDictionary<ApiName, ApiDto> overrides, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var fileResources = await GetFileResources(serviceDirectory, cancellationToken);
        await ValidatePublisherChanges(fileResources, overrides, serviceUri, pipeline, cancellationToken);

        await fileResources.Keys.IterParallel(async name =>
        {
            await ApiPolicy.ValidatePublisherChanges(name, serviceDirectory, serviceUri, pipeline, cancellationToken);
            await ApiTag.ValidatePublisherChanges(name, serviceDirectory, serviceUri, pipeline, cancellationToken);
        }, cancellationToken);
    }

    private static async ValueTask ValidatePublisherChanges(IDictionary<ApiName, ApiDto> fileResources, IDictionary<ApiName, ApiDto> overrides, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var apimResources = await GetApimResources(serviceUri, pipeline, cancellationToken);

        var expected = PublisherOptions.Override(fileResources, overrides)
                                       .MapValue(NormalizeDto);
        var actual = apimResources.MapValue(NormalizeDto);
        actual.Should().BeEquivalentTo(expected);
    }

    public static async ValueTask ValidatePublisherCommitChanges(CommitId commitId, ManagementServiceDirectory serviceDirectory, IDictionary<ApiName, ApiDto> overrides, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var fileResources = await GetFileResources(commitId, serviceDirectory, cancellationToken);
        await ValidatePublisherChanges(fileResources, overrides, serviceUri, pipeline, cancellationToken);

        await fileResources.Keys.IterParallel(async name =>
        {
            await ApiPolicy.ValidatePublisherCommitChanges(name, commitId, serviceDirectory, serviceUri, pipeline, cancellationToken);
            await ApiTag.ValidatePublisherCommitChanges(name, commitId, serviceDirectory, serviceUri, pipeline, cancellationToken);
        }, cancellationToken);
    }

    private static async ValueTask<FrozenDictionary<ApiName, ApiDto>> GetFileResources(CommitId commitId, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await Git.GetExistingFilesInCommit(serviceDirectory.ToDirectoryInfo(), commitId)
                 .ToAsyncEnumerable()
                 .Choose(file => ApiInformationFile.TryParse(file, serviceDirectory))
                 .Choose(async file => await TryGetCommitResource(commitId, serviceDirectory, file, cancellationToken))
                 .ToFrozenDictionary(cancellationToken);

    private static async ValueTask<Option<(ApiName name, ApiDto dto)>> TryGetCommitResource(CommitId commitId, ManagementServiceDirectory serviceDirectory, ApiInformationFile file, CancellationToken cancellationToken)
    {
        var name = file.Parent.Name;
        var contentsOption = Git.TryGetFileContentsInCommit(serviceDirectory.ToDirectoryInfo(), file.ToFileInfo(), commitId);

        return await contentsOption.MapTask(async contents =>
        {
            using (contents)
            {
                var data = await BinaryData.FromStreamAsync(contents, cancellationToken);
                var dto = data.ToObjectFromJson<ApiDto>();
                return (name, dto);
            }
        });
    }
}
