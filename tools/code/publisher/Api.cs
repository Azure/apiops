using AsyncKeyedLock;
using Azure.Core.Pipeline;
using common;
using DotNext.Threading;
using LanguageExt;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Exceptions;
using Microsoft.OpenApi.Extensions;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers;
using Nito.Comparers.Linq;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace publisher;

internal delegate Option<PublisherAction> FindApiAction(FileInfo file);

file delegate Option<ApiName> TryParseApiName(FileInfo file);

file delegate ValueTask ProcessApi(ApiName name, CancellationToken cancellationToken);

file delegate bool IsApiNameInSourceControl(ApiName name);

internal delegate ValueTask PutApi(ApiName name, CancellationToken cancellationToken);

file delegate ValueTask<Option<ApiDto>> FindApiDto(ApiName name, CancellationToken cancellationToken);

file delegate ValueTask CorrectApimRevisionNumber(ApiName name, ApiDto Dto, CancellationToken cancellationToken);

file delegate FrozenDictionary<ApiName, Func<CancellationToken, ValueTask<Option<ApiDto>>>> GetApiDtosInPreviousCommit();

file delegate ValueTask PutApiInApim(ApiName name, ApiDto dto, CancellationToken cancellationToken);

file delegate ValueTask DeleteApiFromApim(ApiName name, CancellationToken cancellationToken);

file delegate ValueTask MakeApiRevisionCurrent(ApiName name, ApiRevisionNumber revisionNumber, CancellationToken cancellationToken);

file delegate ValueTask DeleteApi(ApiName name, CancellationToken cancellationToken);

internal delegate ValueTask OnDeletingApi(ApiName name, CancellationToken cancellationToken);

file sealed class FindApiActionHandler(TryParseApiName tryParseName, ProcessApi processApi)
{
    public Option<PublisherAction> Handle(FileInfo file) =>
        from name in tryParseName(file)
        select GetPublisherAction(name);

    private PublisherAction GetPublisherAction(ApiName name) =>
        async cancellationToken => await processApi(name, cancellationToken);
}

file sealed class TryParseApiNameHandler(ManagementServiceDirectory serviceDirectory)
{
    public Option<ApiName> Handle(FileInfo file) =>
        TryParseNameFromInformationFile(file)
        | TryParseNameFromSpecificationFile(file);

    private Option<ApiName> TryParseNameFromInformationFile(FileInfo file) =>
        from informationFile in ApiInformationFile.TryParse(file, serviceDirectory)
        select informationFile.Parent.Name;

    private Option<ApiName> TryParseNameFromSpecificationFile(FileInfo file) =>
        from apiDirectory in ApiDirectory.TryParse(file.Directory, serviceDirectory)
        where Common.SpecificationFileNames.Contains(file.Name)
        select apiDirectory.Name;
}

/// <summary>
/// Limits the number of simultaneous operations.
/// </summary>
file sealed class ApiSemaphore : IDisposable
{
    private readonly AsyncKeyedLocker<ApiName> locker = new(LockOptions.Default);
    private ImmutableHashSet<ApiName> processedNames = [];

    /// <summary>
    /// Runs the provided action, ensuring that each name is processed only once.
    /// </summary>
    public async ValueTask Run(ApiName name, Func<ApiName, CancellationToken, ValueTask> action, CancellationToken cancellationToken)
    {
        // Do not process the same name simultaneously
        using var _ = await locker.LockAsync(name, cancellationToken).ConfigureAwait(false);

        // Only process each name once
        if (processedNames.Contains(name))
        {
            return;
        }

        await action(name, cancellationToken);

        ImmutableInterlocked.Update(ref processedNames, set => set.Add(name));
    }

    public void Dispose() => locker.Dispose();
}

file sealed class ProcessApiHandler(IsApiNameInSourceControl isNameInSourceControl, PutApi put, DeleteApi delete) : IDisposable
{
    private readonly ApiSemaphore semaphore = new();

    public async ValueTask Handle(ApiName name, CancellationToken cancellationToken) =>
        await semaphore.Run(name, HandleInner, cancellationToken);

    private async ValueTask HandleInner(ApiName name, CancellationToken cancellationToken)
    {
        // Process root API first
        if (ApiName.IsRevisioned(name))
        {
            var rootName = ApiName.GetRootName(name);
            await Handle(rootName, cancellationToken);
        }

        if (isNameInSourceControl(name))
        {
            await put(name, cancellationToken);
        }
        else
        {
            await delete(name, cancellationToken);
        }
    }

    public void Dispose() => semaphore.Dispose();
}

file sealed class IsApiNameInSourceControlHandler(GetArtifactFiles getArtifactFiles, ManagementServiceDirectory serviceDirectory)
{
    public bool Handle(ApiName name) =>
        DoesInformationFileExist(name)
        || DoesSpecificationFileExist(name);

    private bool DoesInformationFileExist(ApiName name)
    {
        var artifactFiles = getArtifactFiles();
        var informationFile = ApiInformationFile.From(name, serviceDirectory);

        return artifactFiles.Contains(informationFile.ToFileInfo());
    }

    private bool DoesSpecificationFileExist(ApiName name)
    {
        var artifactFiles = getArtifactFiles();
        var getFileInApiDirectory = ApiDirectory.From(name, serviceDirectory)
                                                .ToDirectoryInfo()
                                                .GetChildFile;

        return Common.SpecificationFileNames
                     .Select(getFileInApiDirectory)
                     .Any(artifactFiles.Contains);
    }
}

file sealed class PutApiHandler(FindApiDto findDto,
                                PutVersionSet putVersionSet,
                                CorrectApimRevisionNumber correctApimRevisionNumber,
                                PutApiInApim putInApim) : IDisposable
{
    private readonly ApiSemaphore semaphore = new();

    public async ValueTask Handle(ApiName name, CancellationToken cancellationToken) =>
        await semaphore.Run(name, Put, cancellationToken);

    private async ValueTask Put(ApiName name, CancellationToken cancellationToken)
    {
        var dtoOption = await findDto(name, cancellationToken);
        await dtoOption.IterTask(async dto => await Put(name, dto, cancellationToken));
    }

    private async ValueTask Put(ApiName name, ApiDto dto, CancellationToken cancellationToken)
    {
        // Put prerequisites
        await PutVersionSet(dto, cancellationToken);
        await PutCurrentRevision(name, dto, cancellationToken);

        await putInApim(name, dto, cancellationToken);
    }

    private async ValueTask PutVersionSet(ApiDto dto, CancellationToken cancellationToken) =>
        await ApiModule.TryGetVersionSetName(dto)
                       .IterTask(putVersionSet.Invoke, cancellationToken);

    private async ValueTask PutCurrentRevision(ApiName name, ApiDto dto, CancellationToken cancellationToken)
    {
        if (ApiName.IsRevisioned(name))
        {
            var rootName = ApiName.GetRootName(name);
            await Handle(rootName, cancellationToken);
        }
        else
        {
            await correctApimRevisionNumber(name, dto, cancellationToken);
        }
    }

    public void Dispose() => semaphore.Dispose();
}

file sealed class FindApiDtoHandler(ManagementServiceDirectory serviceDirectory,
                                    TryGetFileContents tryGetFileContents,
                                    GetArtifactFiles getArtifactFiles,
                                    OverrideDtoFactory overrideDtoFactory)
{
    public async ValueTask<Option<ApiDto>> Handle(ApiName name, CancellationToken cancellationToken)
    {
        var informationFileDtoOption = await TryGetInformationFileDto(name, cancellationToken);
        var specificationContentsOption = await TryGetSpecificationContents(name, cancellationToken);

        return await TryGetDto(name, informationFileDtoOption, specificationContentsOption, cancellationToken);
    }

    private async ValueTask<Option<ApiDto>> TryGetInformationFileDto(ApiName name, CancellationToken cancellationToken)
    {
        var informationFile = ApiInformationFile.From(name, serviceDirectory);
        var contentsOption = await tryGetFileContents(informationFile.ToFileInfo(), cancellationToken);

        return from contents in contentsOption
               select contents.ToObjectFromJson<ApiDto>();
    }

    private async ValueTask<Option<(ApiSpecification, BinaryData)>> TryGetSpecificationContents(ApiName name, CancellationToken cancellationToken) =>
        await GetSpecificationFiles(name)
                .ToAsyncEnumerable()
                .Choose(async file => await TryGetSpecificationContents(file, cancellationToken))
                .HeadOrNone(cancellationToken);

    private FrozenSet<FileInfo> GetSpecificationFiles(ApiName name)
    {
        var apiDirectory = ApiDirectory.From(name, serviceDirectory);
        var artifactFiles = getArtifactFiles();

        return Common.SpecificationFileNames
                     .Select(apiDirectory.ToDirectoryInfo().GetChildFile)
                     .Where(artifactFiles.Contains)
                     .ToFrozenSet();
    }

    private async ValueTask<Option<(ApiSpecification, BinaryData)>> TryGetSpecificationContents(FileInfo file, CancellationToken cancellationToken)
    {
        var contentsOption = await tryGetFileContents(file, cancellationToken);

        return await contentsOption.BindTask(async contents =>
        {
            var specificationFileOption = await TryParseSpecificationFile(file, contents, cancellationToken);

            return from specificationFile in specificationFileOption
                   select (specificationFile.Specification, contents);
        });
    }

    private async ValueTask<Option<ApiSpecificationFile>> TryParseSpecificationFile(FileInfo file, BinaryData contents, CancellationToken cancellationToken) =>
        await ApiSpecificationFile.TryParse(file,
                                            getFileContents: _ => ValueTask.FromResult(contents),
                                            serviceDirectory,
                                            cancellationToken);

    private async ValueTask<Option<ApiDto>> TryGetDto(ApiName name,
                                                      Option<ApiDto> informationFileDtoOption,
                                                      Option<(ApiSpecification, BinaryData)> specificationContentsOption,
                                                      CancellationToken cancellationToken)
    {
        if (informationFileDtoOption.IsNone && specificationContentsOption.IsNone)
        {
            return Option<ApiDto>.None;
        }

#pragma warning disable CA1849 // Call async methods when in an async method
        var dto = informationFileDtoOption.IfNone(() => new ApiDto { Properties = new ApiDto.ApiCreateOrUpdateProperties() });
#pragma warning restore CA1849 // Call async methods when in an async method
        await specificationContentsOption.IterTask(async specificationContents =>
        {
            var (specification, contents) = specificationContents;
            dto = await AddSpecificationToDto(name, dto, specification, contents, cancellationToken);
        });

        var overrideDto = overrideDtoFactory.Create<ApiName, ApiDto>();
        dto = overrideDto(name, dto);

        return dto;
    }

    private static async ValueTask<ApiDto> AddSpecificationToDto(ApiName name, ApiDto dto, ApiSpecification specification, BinaryData contents, CancellationToken cancellationToken) =>
        dto with
        {
            Properties = dto.Properties with
            {
                Format = specification switch
                {
                    ApiSpecification.Wsdl => "wsdl",
                    ApiSpecification.Wadl => "wadl-xml",
                    ApiSpecification.OpenApi openApi => (openApi.Format, openApi.Version) switch
                    {
                        (common.OpenApiFormat.Json, OpenApiVersion.V2) => "swagger-json",
                        (common.OpenApiFormat.Json, OpenApiVersion.V3) => "openapi+json",
                        (common.OpenApiFormat.Yaml, OpenApiVersion.V2) => "openapi",
                        (common.OpenApiFormat.Yaml, OpenApiVersion.V3) => "openapi",
                        _ => throw new InvalidOperationException($"Unsupported OpenAPI format '{openApi.Format}' and version '{openApi.Version}'.")
                    },
                    _ => dto.Properties.Format
                },
                // APIM does not support OpenAPI V2 YAML. Convert to V3 YAML if needed.
                Value = specification switch
                {
                    ApiSpecification.GraphQl => null,
                    ApiSpecification.OpenApi { Format: common.OpenApiFormat.Yaml, Version: OpenApiVersion.V2 } =>
                        await ConvertStreamToOpenApiV3Yaml(contents, $"Could not convert specification for API {name} to OpenAPIV3.", cancellationToken),
                    _ => contents.ToString()
                }
            }
        };

    private static async ValueTask<string> ConvertStreamToOpenApiV3Yaml(BinaryData contents, string errorMessage, CancellationToken cancellationToken)
    {
        using var stream = contents.ToStream();
        var readResult = await new OpenApiStreamReader().ReadAsync(stream, cancellationToken);

        return readResult.OpenApiDiagnostic.Errors switch
        {
        [] => readResult.OpenApiDocument.Serialize(OpenApiSpecVersion.OpenApi3_0, Microsoft.OpenApi.OpenApiFormat.Yaml),
            var errors => throw OpenApiErrorsToException(errorMessage, errors)
        };
    }

    private static OpenApiException OpenApiErrorsToException(string message, IEnumerable<OpenApiError> errors) =>
        new($"{message}. Errors are: {Environment.NewLine}{string.Join(Environment.NewLine, errors)}");
}

file sealed class CorrectApimRevisionHandler(ILoggerFactory loggerFactory,
                                             GetApiDtosInPreviousCommit getDtosInPreviousCommit,
                                             PutApiInApim putApiInApim,
                                             DeleteApiFromApim deleteApiFromApim,
                                             IsApiNameInSourceControl isNameInSourceControl,
                                             MakeApiRevisionCurrent makeApiRevisionCurrent)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    /// <summary>
    /// If this is the current revision and its revision number changed,
    /// create a new release with the new revision number in APIM.
    public async ValueTask Handle(ApiName name, ApiDto dto, CancellationToken cancellationToken)
    {
        if (ApiName.IsRevisioned(name))
        {
            return;
        }

        var previousRevisionNumberOption = await FindPreviousRevisionNumber(name, cancellationToken);
        await previousRevisionNumberOption.IterTask(async previousRevisionNumber =>
        {
            var currentRevisionNumber = Common.GetRevisionNumber(dto);
            await SetApimCurrentRevisionNumber(name, currentRevisionNumber, previousRevisionNumber, cancellationToken);
        });
    }

    private async ValueTask<Option<ApiRevisionNumber>> FindPreviousRevisionNumber(ApiName name, CancellationToken cancellationToken) =>
        await getDtosInPreviousCommit()
                .Find(name)
                .BindTask(async getDto =>
                {
                    var dtoOption = await getDto(cancellationToken);

                    return from dto in dtoOption
                           select Common.GetRevisionNumber(dto);
                });

    private async ValueTask SetApimCurrentRevisionNumber(ApiName name, ApiRevisionNumber newRevisionNumber, ApiRevisionNumber existingRevisionNumber, CancellationToken cancellationToken)
    {
        if (newRevisionNumber == existingRevisionNumber)
        {
            return;
        }

        logger.LogInformation("Changing current revision on {ApiName} from {RevisionNumber} to {RevisionNumber}...", name, existingRevisionNumber, newRevisionNumber);

        await PutRevision(name, newRevisionNumber, existingRevisionNumber, cancellationToken);
        await makeApiRevisionCurrent(name, newRevisionNumber, cancellationToken);
        await DeleteOldRevision(name, existingRevisionNumber, cancellationToken);
    }

    private async ValueTask PutRevision(ApiName name, ApiRevisionNumber revisionNumber, ApiRevisionNumber existingRevisionNumber, CancellationToken cancellationToken)
    {
        var dto = new ApiDto
        {
            Properties = new ApiDto.ApiCreateOrUpdateProperties
            {
                ApiRevision = revisionNumber.ToString(),
                SourceApiId = $"/apis/{ApiName.GetRevisionedName(name, existingRevisionNumber)}"
            }
        };

        await putApiInApim(name, dto, cancellationToken);
    }

    /// <summary>
    /// If the old revision is no longer in source control, delete it from APIM. Handles this scenario:
    /// 1. Dev and prod APIM both have apiA with current revision 1. Artifacts folder has /apis/apiA/apiInformation.json with revision 1.
    /// 2. User changes the current revision in dev APIM from 1 to 2.
    /// 3. User deletes revision 1 from dev APIM, as it's no longer needed.
    /// 4. User runs extractor for dev APIM. Artifacts folder has /apis/apiA/apiInformation.json with revision 2.
    /// 5. User runs publisher to prod APIM. The only changed artifact will be an update in apiInformation.json to revision 2, so we will create revision 2 in prod and make it current.
    /// 
    /// If we do nothing else, dev and prod will be inconsistent as prod will still have the revision 1 API. There was nothing in Git that told the publisher to delete revision 1.
    /// </summary>
    private async ValueTask DeleteOldRevision(ApiName name, ApiRevisionNumber oldRevisionNumber, CancellationToken cancellationToken)
    {
        var revisionedName = ApiName.GetRevisionedName(name, oldRevisionNumber);

        if (isNameInSourceControl(revisionedName))
        {
            return;
        }

        logger.LogInformation("Deleting old revision {RevisionNumber} of {ApiName}...", oldRevisionNumber, name);
        await deleteApiFromApim(revisionedName, cancellationToken);
    }
}

file sealed class GetApiDtosInPreviousCommitHandler(GetArtifactsInPreviousCommit getArtifactsInPreviousCommit, ManagementServiceDirectory serviceDirectory)
{
    private readonly Lazy<FrozenDictionary<ApiName, Func<CancellationToken, ValueTask<Option<ApiDto>>>>> dtosInPreviousCommit = new(() => GetDtos(getArtifactsInPreviousCommit, serviceDirectory));

    public FrozenDictionary<ApiName, Func<CancellationToken, ValueTask<Option<ApiDto>>>> Handle() =>
        dtosInPreviousCommit.Value;

    private static FrozenDictionary<ApiName, Func<CancellationToken, ValueTask<Option<ApiDto>>>> GetDtos(GetArtifactsInPreviousCommit getArtifactsInPreviousCommit,
                                                                                                         ManagementServiceDirectory serviceDirectory) =>
        getArtifactsInPreviousCommit()
            .Choose(kvp => from apiName in TryGetNameFromInformationFile(kvp.Key, serviceDirectory)
                           select (apiName, TryGetDto(kvp.Value)))
            .ToFrozenDictionary();

    private static Option<ApiName> TryGetNameFromInformationFile(FileInfo file, ManagementServiceDirectory serviceDirectory) =>
        from informationFile in ApiInformationFile.TryParse(file, serviceDirectory)
        select informationFile.Parent.Name;

    private static Func<CancellationToken, ValueTask<Option<ApiDto>>> TryGetDto(Func<CancellationToken, ValueTask<Option<BinaryData>>> tryGetContents) =>
        async cancellationToken =>
        {
            var contentsOption = await tryGetContents(cancellationToken);

            return from contents in contentsOption
                   select contents.ToObjectFromJson<ApiDto>();
        };
}

file sealed class PutApiInApimHandler(ILoggerFactory loggerFactory, ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(ApiName name, ApiDto dto, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting API {ApiName}...", name);

        var revisionNumber = Common.GetRevisionNumber(dto);
        var uri = GetRevisionedUri(name, revisionNumber);

        // APIM sometimes fails revisions if isCurrent is set to true.
        var dtoWithoutIsCurrent = dto with { Properties = dto.Properties with { IsCurrent = null } };
        await uri.PutDto(dtoWithoutIsCurrent, pipeline, cancellationToken);
    }

    private ApiUri GetRevisionedUri(ApiName name, ApiRevisionNumber revisionNumber)
    {
        var revisionedApiName = ApiName.GetRevisionedName(name, revisionNumber);
        return ApiUri.From(revisionedApiName, serviceUri);
    }
}

file sealed class DeleteApiFromApimHandler(ILoggerFactory loggerFactory, ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(ApiName name, CancellationToken cancellationToken)
    {
        logger.LogInformation("Deleting API {ApiName}...", name);

        var uri = ApiUri.From(name, serviceUri);
        if (ApiName.IsRevisioned(name))
        {
            await uri.Delete(pipeline, cancellationToken);
        }
        else
        {
            await uri.DeleteAllRevisions(pipeline, cancellationToken);
        }
    }
}

file sealed class MakeApiRevisionCurrentHandler(PutApiReleaseInApim putRelease, DeleteApiReleaseFromApim deleteRelease)
{
    public async ValueTask Handle(ApiName name, ApiRevisionNumber revisionNumber, CancellationToken cancellationToken)
    {
        var revisionedName = ApiName.GetRevisionedName(name, revisionNumber);
        var releaseName = ApiReleaseName.From("apiops-set-current");
        var releaseDto = new ApiReleaseDto
        {
            Properties = new ApiReleaseDto.ApiReleaseContract
            {
                ApiId = $"/apis/{revisionedName}",
                Notes = "Setting current revision for ApiOps"
            }
        };

        await putRelease(releaseName, releaseDto, name, cancellationToken);
        await deleteRelease(releaseName, name, cancellationToken);
    }
}

file sealed class DeleteApiHandler(IEnumerable<OnDeletingApi> onDeletingHandlers,
                                   ILoggerFactory loggerFactory,
                                   FindApiDto findDto,
                                   DeleteApiFromApim deleteFromApim) : IDisposable
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);
    private readonly ApiSemaphore semaphore = new();

    public async ValueTask Handle(ApiName name, CancellationToken cancellationToken) =>
        await semaphore.Run(name, Delete, cancellationToken);

    private async ValueTask Delete(ApiName name, CancellationToken cancellationToken)
    {
        if (await IsApiRevisionNumberCurrentInSourceControl(name, cancellationToken))
        {
            logger.LogInformation("API {ApiName} is the current revision in source control. Skipping deletion...", name);
            return;
        }

        await onDeletingHandlers.IterParallel(async handler => await handler(name, cancellationToken), cancellationToken);
        await deleteFromApim(name, cancellationToken);
    }

    /// <summary>
    /// We don't want to delete a revision if it was just made current. For instance:
    /// 1. Dev has apiA  with revision 1 (current) and revision 2. Artifacts folder has:
    ///     - /apis/apiA/apiInformation.json with revision 1 as current
    ///     - /apis/apiA;rev=2/apiInformation.json
    /// 2. User makes revision 2 current in dev APIM.
    /// 3. User runs extractor for dev APIM. Artifacts folder has:
    ///     - /apis/apiA/apiInformation.json with revision 2 as current
    ///     - /apis/apiA;rev=1/apiInformation.json
    ///     - /apis/apiA;rev=2 folder gets deleted.
    /// 4. User runs publisher to prod APIM. We don't want to handle the deletion of folder /apis/apiA;rev=2, as it's the current revision.
    private async ValueTask<bool> IsApiRevisionNumberCurrentInSourceControl(ApiName name, CancellationToken cancellationToken) =>
        await ApiName.TryParseRevisionedName(name)
                     .Match(async api =>
                     {
                         var (rootName, revisionNumber) = api;
                         var sourceControlRevisionNumberOption = await TryGetRevisionNumberInSourceControl(rootName, cancellationToken);

#pragma warning disable CA1849 // Call async methods when in an async method
                         return sourceControlRevisionNumberOption
                                .Map(sourceControlRevisionNumber => sourceControlRevisionNumber == revisionNumber)
                                .IfNone(false);
#pragma warning restore CA1849 // Call async methods when in an async method
                     }, async _ => await ValueTask.FromResult(false));

    private async ValueTask<Option<ApiRevisionNumber>> TryGetRevisionNumberInSourceControl(ApiName name, CancellationToken cancellationToken)
    {
        var dtoOption = await findDto(name, cancellationToken);

        return dtoOption.Map(Common.GetRevisionNumber);
    }

    public void Dispose() => semaphore.Dispose();
}

file sealed class OnDeletingVersionSetHandler(GetApiDtosInPreviousCommit getDtosInPreviousCommit, ProcessApi processApi)
{
    private readonly AsyncLazy<FrozenDictionary<VersionSetName, FrozenSet<ApiName>>> getVersionSetApis = new(async cancellationToken => await GetVersionSetApis(getDtosInPreviousCommit, cancellationToken));

    /// <summary>
    /// If a version set is about to be deleted, process the APIs that reference it
    /// </summary>
    public async ValueTask Handle(VersionSetName name, CancellationToken cancellationToken)
    {
        var apis = await GetVersionSetApis(name, cancellationToken);

        await apis.IterParallel(processApi.Invoke, cancellationToken);
    }

    private static async ValueTask<FrozenDictionary<VersionSetName, FrozenSet<ApiName>>> GetVersionSetApis(GetApiDtosInPreviousCommit getDtosInPreviousCommit, CancellationToken cancellationToken) =>
        await getDtosInPreviousCommit()
                .ToAsyncEnumerable()
                .Choose(async kvp =>
                {
                    var dtoOption = await kvp.Value(cancellationToken);

                    return from dto in dtoOption
                           from versionSetName in ApiModule.TryGetVersionSetName(dto)
                           select (VersionSetName: versionSetName, ApiName: kvp.Key);
                })
                .GroupBy(x => x.VersionSetName, x => x.ApiName)
                .SelectAwait(async group => (group.Key, await group.ToFrozenSet(cancellationToken)))
                .ToFrozenDictionary(cancellationToken);

    private async ValueTask<FrozenSet<ApiName>> GetVersionSetApis(VersionSetName name, CancellationToken cancellationToken)
    {
        var versionSetApis = await getVersionSetApis.WithCancellation(cancellationToken);

#pragma warning disable CA1849 // Call async methods when in an async method
        return versionSetApis.Find(name)
                             .IfNone(FrozenSet<ApiName>.Empty);
#pragma warning restore CA1849 // Call async methods when in an async method
    }
}

internal static class ApiServices
{
    public static void ConfigureFindApiAction(IServiceCollection services)
    {
        ConfigureTryParseApiName(services);
        ConfigureProcessApi(services);

        services.TryAddSingleton<FindApiActionHandler>();
        services.TryAddSingleton<FindApiAction>(provider => provider.GetRequiredService<FindApiActionHandler>().Handle);
    }

    private static void ConfigureTryParseApiName(IServiceCollection services)
    {
        services.TryAddSingleton<TryParseApiNameHandler>();
        services.TryAddSingleton<TryParseApiName>(provider => provider.GetRequiredService<TryParseApiNameHandler>().Handle);
    }

    private static void ConfigureProcessApi(IServiceCollection services)
    {
        ConfigureIsApiNameInSourceControl(services);
        ConfigurePutApi(services);
        ConfigureDeleteApi(services);

        services.TryAddSingleton<ProcessApiHandler>();
        services.TryAddSingleton<ProcessApi>(provider => provider.GetRequiredService<ProcessApiHandler>().Handle);
    }

    private static void ConfigureIsApiNameInSourceControl(IServiceCollection services)
    {
        services.TryAddSingleton<IsApiNameInSourceControlHandler>();
        services.TryAddSingleton<IsApiNameInSourceControl>(provider => provider.GetRequiredService<IsApiNameInSourceControlHandler>().Handle);
    }

    public static void ConfigurePutApi(IServiceCollection services)
    {
        ConfigureFindApiDto(services);
        VersionSetServices.ConfigurePutVersionSet(services);
        ConfigureCorrectApimRevisionNumber(services);
        ConfigurePutApiInApim(services);

        services.TryAddSingleton<PutApiHandler>();
        services.TryAddSingleton<PutApi>(provider => provider.GetRequiredService<PutApiHandler>().Handle);
    }

    private static void ConfigureFindApiDto(IServiceCollection services)
    {
        services.TryAddSingleton<FindApiDtoHandler>();
        services.TryAddSingleton<FindApiDto>(provider => provider.GetRequiredService<FindApiDtoHandler>().Handle);
    }

    private static void ConfigureCorrectApimRevisionNumber(IServiceCollection services)
    {
        ConfigureGetApiDtosInPreviousCommit(services);
        ConfigurePutApiInApim(services);
        ConfigureDeleteApiFromApim(services);
        ConfigureIsApiNameInSourceControl(services);
        ConfigureMakeApiRevisionCurrent(services);

        services.TryAddSingleton<CorrectApimRevisionHandler>();
        services.TryAddSingleton<CorrectApimRevisionNumber>(provider => provider.GetRequiredService<CorrectApimRevisionHandler>().Handle);
    }

    private static void ConfigureGetApiDtosInPreviousCommit(IServiceCollection services)
    {
        services.TryAddSingleton<GetApiDtosInPreviousCommitHandler>();
        services.TryAddSingleton<GetApiDtosInPreviousCommit>(provider => provider.GetRequiredService<GetApiDtosInPreviousCommitHandler>().Handle);
    }

    private static void ConfigurePutApiInApim(IServiceCollection services)
    {
        services.TryAddSingleton<PutApiInApimHandler>();
        services.TryAddSingleton<PutApiInApim>(provider => provider.GetRequiredService<PutApiInApimHandler>().Handle);
    }

    private static void ConfigureDeleteApiFromApim(IServiceCollection services)
    {
        services.TryAddSingleton<DeleteApiFromApimHandler>();
        services.TryAddSingleton<DeleteApiFromApim>(provider => provider.GetRequiredService<DeleteApiFromApimHandler>().Handle);
    }

    private static void ConfigureMakeApiRevisionCurrent(IServiceCollection services)
    {
        ApiReleaseServices.ConfigurePutApiReleaseInApim(services);
        ApiReleaseServices.ConfigureDeleteApiReleaseFromApim(services);

        services.TryAddSingleton<MakeApiRevisionCurrentHandler>();
        services.TryAddSingleton<MakeApiRevisionCurrent>(provider => provider.GetRequiredService<MakeApiRevisionCurrentHandler>().Handle);
    }

    private static void ConfigureDeleteApi(IServiceCollection services)
    {
        ConfigureOnDeletingApi(services);
        ConfigureFindApiDto(services);
        ConfigureDeleteApiFromApim(services);

        services.TryAddSingleton<DeleteApiHandler>();
        services.TryAddSingleton<DeleteApi>(provider => provider.GetRequiredService<DeleteApiHandler>().Handle);
    }

    private static void ConfigureOnDeletingApi(IServiceCollection services)
    {
        SubscriptionServices.ConfigureOnDeletingApi(services);
    }

    public static void ConfigureOnDeletingVersionSet(IServiceCollection services)
    {
        ConfigureGetApiDtosInPreviousCommit(services);
        ConfigureProcessApi(services);

        services.TryAddSingleton<OnDeletingVersionSetHandler>();
        services.TryAddSingleton<OnDeletingVersionSet>(provider => provider.GetRequiredService<OnDeletingVersionSetHandler>().Handle);
    }
}

file static class Common
{
    public static ILogger GetLogger(ILoggerFactory loggerFactory) =>
        loggerFactory.CreateLogger("ApiPublisher");

    public static FrozenSet<string> SpecificationFileNames { get; } =
        new[]
        {
            WadlSpecificationFile.Name,
            WsdlSpecificationFile.Name,
            GraphQlSpecificationFile.Name,
            JsonOpenApiSpecificationFile.Name,
            YamlOpenApiSpecificationFile.Name
        }.ToFrozenSet();

    public static ApiRevisionNumber GetRevisionNumber(ApiDto dto) =>
        ApiRevisionNumber.TryFrom(dto.Properties.ApiRevision)
                         .IfNone(() => ApiRevisionNumber.From(1));
}