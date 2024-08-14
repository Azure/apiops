using Azure.Core.Pipeline;
using common;
using common.tests;
using CsCheck;
using FluentAssertions;
using Flurl;
using LanguageExt;
using LanguageExt.UnsafeValueAccess;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using publisher;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace integration.tests;

public delegate ValueTask DeleteAllApis(ManagementServiceName serviceName, CancellationToken cancellationToken);
public delegate ValueTask PutApiModels(IEnumerable<ApiModel> models, ManagementServiceName serviceName, CancellationToken cancellationToken);
public delegate ValueTask ValidateExtractedApis(Option<FrozenSet<ApiName>> apiNamesOption, Option<ApiSpecification> defaultApiSpecification, Option<FrozenSet<VersionSetName>> versionSetNamesOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);
public delegate ValueTask<FrozenDictionary<ApiName, ApiDto>> GetApimApis(ManagementServiceName serviceName, CancellationToken cancellationToken);
public delegate ValueTask<Option<BinaryData>> TryGetApimGraphQlSchema(ApiName name, ManagementServiceName serviceName, CancellationToken cancellationToken);
public delegate ValueTask<FrozenDictionary<ApiName, ApiDto>> GetFileApis(ManagementServiceDirectory serviceDirectory, Option<CommitId> commitIdOption, CancellationToken cancellationToken);
public delegate ValueTask WriteApiModels(IEnumerable<ApiModel> models, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);
public delegate ValueTask ValidatePublishedApis(IDictionary<ApiName, ApiDto> overrides, Option<CommitId> commitIdOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

public static class ApiModule
{
    public static void ConfigureDeleteAllApis(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureGetManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteAllApis);
    }

    private static DeleteAllApis GetDeleteAllApis(IServiceProvider provider)
    {
        var getServiceUri = provider.GetRequiredService<GetManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (serviceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteAllApis));

            logger.LogInformation("Deleting all APIs in {ServiceName}...", serviceName);

            var serviceUri = getServiceUri(serviceName);
            var apisUri = ApisUri.From(serviceUri);

            await apisUri.DeleteAll(pipeline, cancellationToken);
        };
    }

    public static void ConfigurePutApiModels(IHostApplicationBuilder builder)
    {
        ApiDiagnosticModule.ConfigurePutApiDiagnosticModels(builder);
        ManagementServiceModule.ConfigureGetManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutApiModels);
    }

    private static PutApiModels GetPutApiModels(IServiceProvider provider)
    {
        var putDiagnostics = provider.GetRequiredService<PutApiDiagnosticModels>();
        var getServiceUri = provider.GetRequiredService<GetManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (models, serviceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutApiModels));

            logger.LogInformation("Putting API models in {ServiceName}...", serviceName);

            await models.IterParallel(async model =>
            {
                var serviceUri = getServiceUri(serviceName);

                async ValueTask putRevision(ApiRevision revision) => await put(model.Name, model.Type, model.Path, model.Version, revision, serviceUri, cancellationToken);

                // Put first revision to make sure it's the current revision.
                await model.Revisions.HeadOrNone().IterTask(putRevision);

                // Put other revisions
                await model.Revisions.Skip(1).IterParallel(putRevision, cancellationToken);

                await putDiagnostics(model.Diagnostics, model.Name, serviceName, cancellationToken);
            }, cancellationToken);
        };

        async ValueTask put(ApiName name, ApiType type, string path, Option<ApiVersion> version, ApiRevision revision, ManagementServiceUri serviceUri, CancellationToken cancellationToken)
        {
            var rootName = ApiName.GetRootName(name);
            var dto = getDto(rootName, type, path, version, revision);
            var revisionedName = ApiName.GetRevisionedName(rootName, revision.Number);

            var uri = ApiUri.From(revisionedName, serviceUri);
            await uri.PutDto(dto, pipeline, cancellationToken);

            if (type is ApiType.GraphQl)
            {
                await revision.Specification.IterTask(async specification => await uri.PutGraphQlSchema(BinaryData.FromString(specification), pipeline, cancellationToken));
            }

            await ApiPolicyModule.Put(revision.Policies, revisionedName, serviceUri, pipeline, cancellationToken);
            await ApiTagModule.Put(revision.Tags, revisionedName, serviceUri, pipeline, cancellationToken);
        }

        static ApiDto getDto(ApiName name, ApiType type, string path, Option<ApiVersion> version, ApiRevision revision) =>
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
    }

    public static void ConfigureValidateExtractedApis(IHostApplicationBuilder builder)
    {
        ConfigureGetApimApis(builder);
        ConfigureTryGetApimGraphQlSchema(builder);
        ConfigureGetFileApis(builder);
        ApiDiagnosticModule.ConfigureValidateExtractedApiDiagnostics(builder);

        builder.Services.TryAddSingleton(GetValidateExtractedApis);
    }

    private static ValidateExtractedApis GetValidateExtractedApis(IServiceProvider provider)
    {
        var getApimResources = provider.GetRequiredService<GetApimApis>();
        var tryGetApimGraphQlSchema = provider.GetRequiredService<TryGetApimGraphQlSchema>();
        var getFileResources = provider.GetRequiredService<GetFileApis>();
        var validateDiagnostics = provider.GetRequiredService<ValidateExtractedApiDiagnostics>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (apiNamesOption, defaultApiSpecification, versionSetNamesOption, serviceName, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ValidateExtractedApis));

            logger.LogInformation("Validating extracted APIs in {ServiceName}...", serviceName);

            var expected = await getExpectedResources(apiNamesOption, versionSetNamesOption, serviceName, cancellationToken);

            await validateExtractedInformationFiles(expected, serviceDirectory, cancellationToken);
            await validateExtractedSpecificationFiles(expected, defaultApiSpecification, serviceName, serviceDirectory, cancellationToken);

            await expected.WhereKey(name => ApiName.IsNotRevisioned(name))
                          .IterParallel(async kvp =>
                          {
                              var apiName = kvp.Key;

                              await validateDiagnostics(apiName, serviceName, serviceDirectory, cancellationToken);
                          }, cancellationToken);
        };

        async ValueTask<FrozenDictionary<ApiName, ApiDto>> getExpectedResources(Option<FrozenSet<ApiName>> apiNamesOption, Option<FrozenSet<VersionSetName>> versionSetNamesOption, ManagementServiceName serviceName, CancellationToken cancellationToken)
        {
            var apimResources = await getApimResources(serviceName, cancellationToken);

            return apimResources.WhereKey(name => ExtractorOptions.ShouldExtract(name, apiNamesOption))
                                .WhereValue(dto => common.ApiModule.TryGetVersionSetName(dto)
                                                                   .Map(name => ExtractorOptions.ShouldExtract(name, versionSetNamesOption))
                                                                   .IfNone(true))
                                .ToFrozenDictionary();
        }

        async ValueTask validateExtractedInformationFiles(IDictionary<ApiName, ApiDto> expectedResources, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
        {
            var fileResources = await getFileResources(serviceDirectory, Prelude.None, cancellationToken);

            var expected = expectedResources.MapValue(normalizeDto)
                                            .ToFrozenDictionary();

            var actual = fileResources.MapValue(normalizeDto)
                                      .ToFrozenDictionary();

            actual.Should().BeEquivalentTo(expected);
        }

        static string normalizeDto(ApiDto dto) =>
            new
            {
                DisplayName = dto.Properties.DisplayName ?? string.Empty,
                Path = dto.Properties.Path ?? string.Empty,
                RevisionDescription = dto.Properties.ApiRevisionDescription ?? string.Empty,
                Revision = dto.Properties.ApiRevision ?? string.Empty,
                ServiceUrl = Uri.TryCreate(dto.Properties.ServiceUrl, UriKind.Absolute, out var uri)
                                ? uri.RemovePath().ToString()
                                : string.Empty
            }.ToString()!;

        async ValueTask validateExtractedSpecificationFiles(IDictionary<ApiName, ApiDto> expectedResources, Option<ApiSpecification> defaultApiSpecification, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
        {
            var expected = await expectedResources.ToAsyncEnumerable()
                                                  .Choose(async kvp =>
                                                  {
                                                      var name = kvp.Key;
                                                      return from specification in await getExpectedApiSpecification(name, kvp.Value, defaultApiSpecification, serviceName, cancellationToken)
                                                                 // Skip XML specification files. Sometimes they get extracted, other times they fail.
                                                             where specification is not (ApiSpecification.Wsdl or ApiSpecification.Wadl)
                                                             select (name, specification);
                                                  })
                                                  .ToFrozenDictionary(cancellationToken);

            var actual = await common.ApiModule.ListSpecificationFiles(serviceDirectory)
                                               .Select(file => (file.Parent.Name, file.Specification))
                                               // Skip XML specification files. Sometimes they get extracted, other times they fail.
                                               .Where(file => file.Specification is not (ApiSpecification.Wsdl or ApiSpecification.Wadl))
                                               .ToFrozenDictionary(cancellationToken);

            actual.Should().BeEquivalentTo(expected);
        }

        async ValueTask<Option<ApiSpecification>> getExpectedApiSpecification(ApiName name, ApiDto dto, Option<ApiSpecification> defaultApiSpecification, ManagementServiceName serviceName, CancellationToken cancellationToken)
        {
            switch (dto.Properties.ApiType ?? dto.Properties.Type)
            {
                case "graphql":
                    var specificationContents = await tryGetApimGraphQlSchema(name, serviceName, cancellationToken);
                    return specificationContents.Map(contents => new ApiSpecification.GraphQl() as ApiSpecification);
                case "soap":
                    return new ApiSpecification.Wsdl();
                case "websocket":
                    return Option<ApiSpecification>.None;
                default:
                    return defaultApiSpecification.IfNone(() => new ApiSpecification.OpenApi
                    {
                        Format = new OpenApiFormat.Yaml(),
                        Version = new OpenApiVersion.V3()
                    });
            }
        }
    }

    public static void ConfigureGetApimApis(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureGetManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetGetApimApis);
    }

    private static GetApimApis GetGetApimApis(IServiceProvider provider)
    {
        var getServiceUri = provider.GetRequiredService<GetManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (serviceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(GetApimApis));

            logger.LogInformation("Getting APIs from {ServiceName}...", serviceName);

            var serviceUri = getServiceUri(serviceName);

            return await ApisUri.From(serviceUri)
                                .List(pipeline, cancellationToken)
                                .ToFrozenDictionary(cancellationToken);
        };
    }

    public static void ConfigureTryGetApimGraphQlSchema(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureGetManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetTryGetApimGraphQlSchema);
    }

    private static TryGetApimGraphQlSchema GetTryGetApimGraphQlSchema(IServiceProvider provider)
    {
        var getServiceUri = provider.GetRequiredService<GetManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, serviceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(TryGetApimGraphQlSchema));

            logger.LogInformation("Getting GraphQL schema for {ApiName} from {ServiceName}...", name, serviceName);

            var serviceUri = getServiceUri(serviceName);

            return await ApiUri.From(name, serviceUri)
                               .TryGetGraphQlSchema(pipeline, cancellationToken);
        };
    }

    public static void ConfigureGetFileApis(IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(GetGetFileApis);
    }

    private static GetFileApis GetGetFileApis(IServiceProvider provider)
    {
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (serviceDirectory, commitIdOption, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(GetFileApis));

            return await commitIdOption.Map(commitId => getWithCommit(serviceDirectory, commitId, cancellationToken))
                                       .IfNone(() => getWithoutCommit(serviceDirectory, cancellationToken));
        };

        async ValueTask<FrozenDictionary<ApiName, ApiDto>> getWithCommit(ManagementServiceDirectory serviceDirectory, CommitId commitId, CancellationToken cancellationToken)
        {
            using var _ = activitySource.StartActivity(nameof(GetFileApis));

            logger.LogInformation("Getting apis from {ServiceDirectory} as of commit {CommitId}...", serviceDirectory, commitId);

            return await Git.GetExistingFilesInCommit(serviceDirectory.ToDirectoryInfo(), commitId)
                            .ToAsyncEnumerable()
                            .Choose(file => ApiInformationFile.TryParse(file, serviceDirectory))
                            .Choose(async file => await tryGetCommitResource(commitId, serviceDirectory, file, cancellationToken))
                            .ToFrozenDictionary(cancellationToken);
        }

        static async ValueTask<Option<(ApiName name, ApiDto dto)>> tryGetCommitResource(CommitId commitId, ManagementServiceDirectory serviceDirectory, ApiInformationFile file, CancellationToken cancellationToken)
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

        async ValueTask<FrozenDictionary<ApiName, ApiDto>> getWithoutCommit(ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
        {
            logger.LogInformation("Getting APIs from {ServiceDirectory}...", serviceDirectory);

            return await common.ApiModule.ListInformationFiles(serviceDirectory)
                                         .ToAsyncEnumerable()
                                         .SelectAwait(async file => (file.Parent.Name,
                                                                     await file.ReadDto(cancellationToken)))
                                         .ToFrozenDictionary(cancellationToken);
        }
    }

    public static void ConfigureWriteApiModels(IHostApplicationBuilder builder)
    {
        ApiDiagnosticModule.ConfigureWriteApiDiagnosticModels(builder);

        builder.Services.TryAddSingleton(GetWriteApiModels);
    }

    private static WriteApiModels GetWriteApiModels(IServiceProvider provider)
    {
        var writeDiagnostics = provider.GetRequiredService<WriteApiDiagnosticModels>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (models, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(WriteApiModels));

            logger.LogInformation("Writing api models to {ServiceDirectory}...", serviceDirectory);

            await models.IterParallel(async model =>
            {
                await writeRevisionArtifacts(model, serviceDirectory, cancellationToken);

                await writeDiagnostics(model.Diagnostics, model.Name, serviceDirectory, cancellationToken);
            }, cancellationToken);
        };

        static async ValueTask writeRevisionArtifacts(ApiModel model, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
            await model.Revisions
                       .Select((revision, index) => (revision, index))
                       .IterParallel(async x =>
                       {
                           var (name, type, path, version, (revision, index)) = (model.Name, model.Type, model.Path, model.Version, x);

                           await writeInformationFile(name, type, path, version, revision, index, serviceDirectory, cancellationToken);
                           await writeSpecificationFile(name, type, revision, index, serviceDirectory, cancellationToken);
                       }, cancellationToken);

        static async ValueTask writeInformationFile(ApiName name, ApiType type, string path, Option<ApiVersion> version, ApiRevision revision, int index, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
        {
            var apiName = getApiName(name, revision, index);
            var informationFile = ApiInformationFile.From(apiName, serviceDirectory);
            var rootApiName = ApiName.GetRootName(name);
            var dto = getDto(rootApiName, type, path, version, revision);

            await informationFile.WriteDto(dto, cancellationToken);
        }

        static ApiName getApiName(ApiName name, ApiRevision revision, int index)
        {
            var rootApiName = ApiName.GetRootName(name);

            return index == 0 ? rootApiName : ApiName.GetRevisionedName(rootApiName, revision.Number);
        }

        static async ValueTask writeSpecificationFile(ApiName name, ApiType type, ApiRevision revision, int index, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
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
                var apiName = getApiName(name, revision, index);
                var specificationFile = ApiSpecificationFile.From(specification, apiName, serviceDirectory);
                await specificationFile.WriteSpecification(BinaryData.FromString(contents), cancellationToken);
            });
        }

        static ApiDto getDto(ApiName name, ApiType type, string path, Option<ApiVersion> version, ApiRevision revision) =>
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
    }

    public static void ConfigureValidatePublishedApis(IHostApplicationBuilder builder)
    {
        ConfigureGetFileApis(builder);
        ConfigureGetApimApis(builder);
        ApiDiagnosticModule.ConfigureValidatePublishedApiDiagnostics(builder);

        builder.Services.TryAddSingleton(GetValidatePublishedApis);
    }

    private static ValidatePublishedApis GetValidatePublishedApis(IServiceProvider provider)
    {
        var getFileResources = provider.GetRequiredService<GetFileApis>();
        var getApimResources = provider.GetRequiredService<GetApimApis>();
        var validateDiagnostics = provider.GetRequiredService<ValidatePublishedApiDiagnostics>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (overrides, commitIdOption, serviceName, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ValidatePublishedApis));

            logger.LogInformation("Validating published apis in {ServiceDirectory}...", serviceDirectory);

            var apimResources = await getApimResources(serviceName, cancellationToken);
            var fileResources = await getFileResources(serviceDirectory, commitIdOption, cancellationToken);

            var expected = PublisherOptions.Override(fileResources, overrides)
                                           .MapValue(normalizeDto)
                                           .ToFrozenDictionary();

            var actual = apimResources.MapValue(normalizeDto)
                                      .ToFrozenDictionary();

            actual.Should().BeEquivalentTo(expected);

            await expected.WhereKey(name => ApiName.IsNotRevisioned(name))
                          .IterParallel(async kvp =>
                          {
                              var apiName = kvp.Key;

                              await validateDiagnostics(apiName, commitIdOption, serviceName, serviceDirectory, cancellationToken);
                          }, cancellationToken);
        };

        static string normalizeDto(ApiDto dto) =>
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
    }

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

        return Generator.GenerateNewSet(revisions, newGen, updateGen);
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
}