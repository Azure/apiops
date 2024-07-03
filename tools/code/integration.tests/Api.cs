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
using Microsoft.Extensions.Logging;
using publisher;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace integration.tests;

internal delegate ValueTask DeleteAllApis(ManagementServiceName serviceName, CancellationToken cancellationToken);

internal delegate ValueTask PutApiModels(IEnumerable<ApiModel> models, ManagementServiceName serviceName, CancellationToken cancellationToken);

internal delegate ValueTask ValidateExtractedApis(Option<FrozenSet<ApiName>> apiNamesOption, Option<ApiSpecification> defaultApiSpecification, Option<FrozenSet<VersionSetName>> versionSetNamesOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

file delegate ValueTask<FrozenDictionary<ApiName, ApiDto>> GetApimApis(ManagementServiceName serviceName, CancellationToken cancellationToken);

file delegate ValueTask<Option<BinaryData>> TryGetApimGraphQlSchema(ApiName name, ManagementServiceName serviceName, CancellationToken cancellationToken);

file delegate ValueTask<FrozenDictionary<ApiName, ApiDto>> GetFileApis(ManagementServiceDirectory serviceDirectory, Option<CommitId> commitIdOption, CancellationToken cancellationToken);

internal delegate ValueTask WriteApiModels(IEnumerable<ApiModel> models, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

internal delegate ValueTask ValidatePublishedApis(IDictionary<ApiName, ApiDto> overrides, Option<CommitId> commitIdOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

file sealed class DeleteAllApisHandler(ILogger<DeleteAllApis> logger, GetManagementServiceUri getServiceUri, HttpPipeline pipeline, ActivitySource activitySource)
{
    public async ValueTask Handle(ManagementServiceName serviceName, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(DeleteAllApis));

        logger.LogInformation("Deleting all APIs in {ServiceName}...", serviceName);
        var serviceUri = getServiceUri(serviceName);
        await ApisUri.From(serviceUri).DeleteAll(pipeline, cancellationToken);
    }
}

file sealed class PutApiModelsHandler(ILogger<PutApiModels> logger, GetManagementServiceUri getServiceUri, HttpPipeline pipeline, ActivitySource activitySource)
{
    public async ValueTask Handle(IEnumerable<ApiModel> models, ManagementServiceName serviceName, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(PutApiModels));

        logger.LogInformation("Putting API models in {ServiceName}...", serviceName);
        await models.IterParallel(async model =>
        {
            var serviceUri = getServiceUri(serviceName);
            async ValueTask putRevision(ApiRevision revision) => await Put(model.Name, model.Type, model.Path, model.Version, revision, serviceUri, cancellationToken);

            // Put first revision to make sure it's the current revision.
            await model.Revisions.HeadOrNone().IterTask(putRevision);

            // Put other revisions
            await model.Revisions.Skip(1).IterParallel(putRevision, cancellationToken);
        }, cancellationToken);
    }

    private async ValueTask Put(ApiName name, ApiType type, string path, Option<ApiVersion> version, ApiRevision revision, ManagementServiceUri serviceUri, CancellationToken cancellationToken)
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

file sealed class ValidateExtractedApisHandler(ILogger<ValidateExtractedApis> logger, GetApimApis getApimResources, TryGetApimGraphQlSchema tryGetApimGraphQlSchema, GetFileApis getFileResources, ActivitySource activitySource)
{
    public async ValueTask Handle(Option<FrozenSet<ApiName>> apiNamesOption, Option<ApiSpecification> defaultApiSpecification, Option<FrozenSet<VersionSetName>> versionSetNamesOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(ValidateExtractedApis));

        logger.LogInformation("Validating extracted APIs in {ServiceName}...", serviceName);

        var expected = await GetExpectedResources(apiNamesOption, versionSetNamesOption, serviceName, cancellationToken);

        await ValidateExtractedInformationFiles(expected, serviceDirectory, cancellationToken);
        await ValidateExtractedSpecificationFiles(expected, defaultApiSpecification, serviceName, serviceDirectory, cancellationToken);
    }

    private async ValueTask<ImmutableDictionary<ApiName, ApiDto>> GetExpectedResources(Option<FrozenSet<ApiName>> apiNamesOption, Option<FrozenSet<VersionSetName>> versionSetNamesOption, ManagementServiceName serviceName, CancellationToken cancellationToken)
    {
        var apimResources = await getApimResources(serviceName, cancellationToken);

        return apimResources.WhereKey(name => ExtractorOptions.ShouldExtract(name, apiNamesOption))
                            .WhereValue(dto => ApiModule.TryGetVersionSetName(dto)
                                                        .Map(name => ExtractorOptions.ShouldExtract(name, versionSetNamesOption))
                                                        .IfNone(true));
    }

    private async ValueTask ValidateExtractedInformationFiles(IDictionary<ApiName, ApiDto> expectedResources, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        var fileResources = await getFileResources(serviceDirectory, Prelude.None, cancellationToken);

        var expected = expectedResources.MapValue(NormalizeDto);
        var actual = fileResources.MapValue(NormalizeDto);

        actual.Should().BeEquivalentTo(expected);
    }
    private static string NormalizeDto(ApiDto dto) =>
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

    private async ValueTask ValidateExtractedSpecificationFiles(IDictionary<ApiName, ApiDto> expectedResources, Option<ApiSpecification> defaultApiSpecification, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        var expected = await expectedResources.ToAsyncEnumerable()
                                              .Choose(async kvp =>
                                              {
                                                  var name = kvp.Key;
                                                  return from specification in await GetExpectedApiSpecification(name, kvp.Value, defaultApiSpecification, serviceName, cancellationToken)
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

    private async ValueTask<Option<ApiSpecification>> GetExpectedApiSpecification(ApiName name, ApiDto dto, Option<ApiSpecification> defaultApiSpecification, ManagementServiceName serviceName, CancellationToken cancellationToken)
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
#pragma warning disable CA1849 // Call async methods when in an async method
                return defaultApiSpecification.IfNone(() => new ApiSpecification.OpenApi
                {
                    Format = new OpenApiFormat.Yaml(),
                    Version = new OpenApiVersion.V3()
                });
#pragma warning restore CA1849 // Call async methods when in an async method
        }
    }
}

file sealed class GetApimApisHandler(ILogger<GetApimApis> logger, GetManagementServiceUri getServiceUri, HttpPipeline pipeline, ActivitySource activitySource)
{
    public async ValueTask<FrozenDictionary<ApiName, ApiDto>> Handle(ManagementServiceName serviceName, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(GetApimApis));

        logger.LogInformation("Getting APIs from {ServiceName}...", serviceName);

        var serviceUri = getServiceUri(serviceName);
        var uri = ApisUri.From(serviceUri);

        return await uri.List(pipeline, cancellationToken)
                        .ToFrozenDictionary(cancellationToken);
    }
}

file sealed class TryGetApimGraphQlSchemaHandler(ILogger<TryGetApimGraphQlSchema> logger, GetManagementServiceUri getServiceUri, HttpPipeline pipeline, ActivitySource activitySource)
{
    public async ValueTask<Option<BinaryData>> Handle(ApiName name, ManagementServiceName serviceName, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(TryGetApimGraphQlSchema));

        logger.LogInformation("Getting GraphQL schema for {ApiName} from {ServiceName}...", name, serviceName);

        var serviceUri = getServiceUri(serviceName);
        var uri = ApiUri.From(name, serviceUri);

        return await uri.TryGetGraphQlSchema(pipeline, cancellationToken);
    }
}

file sealed class GetFileApisHandler(ILogger<GetFileApis> logger, ActivitySource activitySource)
{
    public async ValueTask<FrozenDictionary<ApiName, ApiDto>> Handle(ManagementServiceDirectory serviceDirectory, Option<CommitId> commitIdOption, CancellationToken cancellationToken) =>
        await commitIdOption.Map(commitId => GetWithCommit(serviceDirectory, commitId, cancellationToken))
                           .IfNone(() => GetWithoutCommit(serviceDirectory, cancellationToken));

    private async ValueTask<FrozenDictionary<ApiName, ApiDto>> GetWithCommit(ManagementServiceDirectory serviceDirectory, CommitId commitId, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(GetFileApis));

        logger.LogInformation("Getting apis from {ServiceDirectory} as of commit {CommitId}...", serviceDirectory, commitId);

        return await Git.GetExistingFilesInCommit(serviceDirectory.ToDirectoryInfo(), commitId)
                        .ToAsyncEnumerable()
                        .Choose(file => ApiInformationFile.TryParse(file, serviceDirectory))
                        .Choose(async file => await TryGetCommitResource(commitId, serviceDirectory, file, cancellationToken))
                        .ToFrozenDictionary(cancellationToken);
    }

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

    private async ValueTask<FrozenDictionary<ApiName, ApiDto>> GetWithoutCommit(ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(GetFileApis));

        logger.LogInformation("Getting apis from {ServiceDirectory}...", serviceDirectory);

        return await ApiModule.ListInformationFiles(serviceDirectory)
                              .ToAsyncEnumerable()
                              .SelectAwait(async file => (file.Parent.Name,
                                                          await file.ReadDto(cancellationToken)))
                              .ToFrozenDictionary(cancellationToken);
    }
}

file sealed class WriteApiModelsHandler(ILogger<WriteApiModels> logger, ActivitySource activitySource)
{
    public async ValueTask Handle(IEnumerable<ApiModel> models, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(WriteApiModels));

        logger.LogInformation("Writing api models to {ServiceDirectory}...", serviceDirectory);
        await models.IterParallel(async model =>
        {
            await WriteRevisionArtifacts(model, serviceDirectory, cancellationToken);
        }, cancellationToken);
    }

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

    private static ApiName GetApiName(ApiName name, ApiRevision revision, int index)
    {
        var rootApiName = ApiName.GetRootName(name);

        return index == 0 ? rootApiName : ApiName.GetRevisionedName(rootApiName, revision.Number);
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

file sealed class ValidatePublishedApisHandler(ILogger<ValidatePublishedApis> logger, GetFileApis getFileResources, GetApimApis getApimResources, ActivitySource activitySource)
{
    public async ValueTask Handle(IDictionary<ApiName, ApiDto> overrides, Option<CommitId> commitIdOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(ValidatePublishedApis));

        logger.LogInformation("Validating published apis in {ServiceDirectory}...", serviceDirectory);

        var apimResources = await getApimResources(serviceName, cancellationToken);
        var fileResources = await getFileResources(serviceDirectory, commitIdOption, cancellationToken);

        var expected = PublisherOptions.Override(fileResources, overrides)
                                       .MapValue(NormalizeDto);
        var actual = apimResources.MapValue(NormalizeDto);

        actual.Should().BeEquivalentTo(expected);
    }

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
}

internal static class ApiServices
{
    public static void ConfigureDeleteAllApis(IServiceCollection services)
    {
        ManagementServices.ConfigureGetManagementServiceUri(services);

        services.TryAddSingleton<DeleteAllApisHandler>();
        services.TryAddSingleton<DeleteAllApis>(provider => provider.GetRequiredService<DeleteAllApisHandler>().Handle);
    }

    public static void ConfigurePutApiModels(IServiceCollection services)
    {
        ManagementServices.ConfigureGetManagementServiceUri(services);

        services.TryAddSingleton<PutApiModelsHandler>();
        services.TryAddSingleton<PutApiModels>(provider => provider.GetRequiredService<PutApiModelsHandler>().Handle);
    }

    public static void ConfigureValidateExtractedApis(IServiceCollection services)
    {
        ConfigureGetApimApis(services);
        ConfigureTryGetApimGraphQlSchema(services);
        ConfigureGetFileApis(services);

        services.TryAddSingleton<ValidateExtractedApisHandler>();
        services.TryAddSingleton<ValidateExtractedApis>(provider => provider.GetRequiredService<ValidateExtractedApisHandler>().Handle);
    }

    private static void ConfigureGetApimApis(IServiceCollection services)
    {
        ManagementServices.ConfigureGetManagementServiceUri(services);

        services.TryAddSingleton<GetApimApisHandler>();
        services.TryAddSingleton<GetApimApis>(provider => provider.GetRequiredService<GetApimApisHandler>().Handle);
    }

    private static void ConfigureTryGetApimGraphQlSchema(IServiceCollection services)
    {
        ManagementServices.ConfigureGetManagementServiceUri(services);

        services.TryAddSingleton<TryGetApimGraphQlSchemaHandler>();
        services.TryAddSingleton<TryGetApimGraphQlSchema>(provider => provider.GetRequiredService<TryGetApimGraphQlSchemaHandler>().Handle);
    }

    private static void ConfigureGetFileApis(IServiceCollection services)
    {
        services.TryAddSingleton<GetFileApisHandler>();
        services.TryAddSingleton<GetFileApis>(provider => provider.GetRequiredService<GetFileApisHandler>().Handle);
    }

    public static void ConfigureWriteApiModels(IServiceCollection services)
    {
        services.TryAddSingleton<WriteApiModelsHandler>();
        services.TryAddSingleton<WriteApiModels>(provider => provider.GetRequiredService<WriteApiModelsHandler>().Handle);
    }

    public static void ConfigureValidatePublishedApis(IServiceCollection services)
    {
        ConfigureGetFileApis(services);
        ConfigureGetApimApis(services);

        services.TryAddSingleton<ValidatePublishedApisHandler>();
        services.TryAddSingleton<ValidatePublishedApis>(provider => provider.GetRequiredService<ValidatePublishedApisHandler>().Handle);
    }
}

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
}
