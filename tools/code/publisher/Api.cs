using Azure.Core.Pipeline;
using common;
using DotNext.Threading;
using LanguageExt;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Exceptions;
using Microsoft.OpenApi.Extensions;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers;
using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace publisher;

public delegate ValueTask PutApis(CancellationToken cancellationToken);
public delegate Option<ApiName> TryParseApiName(FileInfo file);
public delegate bool IsApiNameInSourceControl(ApiName name);
public delegate ValueTask PutApi(ApiName name, CancellationToken cancellationToken);
public delegate ValueTask<Option<ApiDto>> FindApiInformationFileDto(ApiName name, CancellationToken cancellationToken);
public delegate ValueTask<Option<(ApiSpecification Specification, BinaryData Contents)>> FindApiSpecificationContents(ApiName name, CancellationToken cancellationToken);
public delegate ValueTask CorrectApimRevisionNumber(ApiName name, ApiDto Dto, CancellationToken cancellationToken);
public delegate FrozenDictionary<ApiName, Func<CancellationToken, ValueTask<Option<ApiDto>>>> GetApiDtosInPreviousCommit();
public delegate ValueTask MakeApiRevisionCurrent(ApiName name, ApiRevisionNumber revisionNumber, CancellationToken cancellationToken);
public delegate ValueTask PutApiInApim(ApiName name, ApiDto dto, Option<(ApiSpecification.GraphQl Specification, BinaryData Contents)> graphQlSpecificationContentsOption, CancellationToken cancellationToken);
public delegate ValueTask DeleteApis(CancellationToken cancellationToken);
public delegate ValueTask DeleteApi(ApiName name, CancellationToken cancellationToken);
public delegate ValueTask DeleteApiFromApim(ApiName name, CancellationToken cancellationToken);

internal static class ApiModule
{
    public static void ConfigurePutApis(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseApiName(builder);
        ConfigureIsApiNameInSourceControl(builder);
        ConfigurePutApi(builder);

        builder.Services.TryAddSingleton(GetPutApis);
    }

    private static PutApis GetPutApis(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseApiName>();
        var isNameInSourceControl = provider.GetRequiredService<IsApiNameInSourceControl>();
        var put = provider.GetRequiredService<PutApi>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(PutApis));

            logger.LogInformation("Putting APIs...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(isNameInSourceControl.Invoke)
                    .Distinct()
                    .IterParallel(put.Invoke, cancellationToken);
        };
    }

    private static void ConfigureTryParseApiName(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetTryParseApiName);
    }

    private static TryParseApiName GetTryParseApiName(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return file => tryParseNameFromInformationFile(file) | tryParseNameFromSpecificationFile(file);

        Option<ApiName> tryParseNameFromInformationFile(FileInfo file) =>
            from informationFile in ApiInformationFile.TryParse(file, serviceDirectory)
            select informationFile.Parent.Name;

        Option<ApiName> tryParseNameFromSpecificationFile(FileInfo file) =>
            from apiDirectory in ApiDirectory.TryParse(file.Directory, serviceDirectory)
            where Common.SpecificationFileNames.Contains(file.Name)
            select apiDirectory.Name;
    }

    private static void ConfigureIsApiNameInSourceControl(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetArtifactFiles(builder);
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetIsApiNameInSourceControl);
    }

    private static IsApiNameInSourceControl GetIsApiNameInSourceControl(IServiceProvider provider)
    {
        var getArtifactFiles = provider.GetRequiredService<GetArtifactFiles>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return name => doesInformationFileExist(name) || doesSpecificationFileExist(name);

        bool doesInformationFileExist(ApiName name)
        {
            var artifactFiles = getArtifactFiles();
            var informationFile = ApiInformationFile.From(name, serviceDirectory);

            return artifactFiles.Contains(informationFile.ToFileInfo());
        }

        bool doesSpecificationFileExist(ApiName name)
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

    private static void ConfigurePutApi(IHostApplicationBuilder builder)
    {
        ConfigureFindApiInformationFileDto(builder);
        ConfigureFindApiSpecificationContents(builder);
        OverrideDtoModule.ConfigureOverrideDtoFactory(builder);
        ConfigureCorrectApimRevisionNumber(builder);
        ConfigurePutApiInApim(builder);

        builder.Services.TryAddSingleton(GetPutApi);
    }

    private static PutApi GetPutApi(IServiceProvider provider)
    {
        var findInformationFileDto = provider.GetRequiredService<FindApiInformationFileDto>();
        var findSpecificationContents = provider.GetRequiredService<FindApiSpecificationContents>();
        var overrideDtoFactory = provider.GetRequiredService<OverrideDtoFactory>();
        var correctRevisionNumber = provider.GetRequiredService<CorrectApimRevisionNumber>();
        var putInApim = provider.GetRequiredService<PutApiInApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        var overrideDto = overrideDtoFactory.Create<ApiName, ApiDto>();
        var taskDictionary = new ConcurrentDictionary<ApiName, AsyncLazy<Unit>>();

        return putApi;

        async ValueTask putApi(ApiName name, CancellationToken cancellationToken)
        {
            using var _ = activitySource.StartActivity(nameof(PutApi))
                                       ?.AddTag("api.name", name);

            await taskDictionary.GetOrAdd(name,
                                          name => new AsyncLazy<Unit>(async cancellationToken =>
                                          {
                                              await putApiInner(name, cancellationToken);
                                              return Unit.Default;
                                          }))
                                .WithCancellation(cancellationToken);
        };

        async ValueTask putApiInner(ApiName name, CancellationToken cancellationToken)
        {
            var informationFileDtoOption = await findInformationFileDto(name, cancellationToken);
            await informationFileDtoOption.IterTask(async informationFileDto =>
            {
                await putCurrentRevision(name, informationFileDto, cancellationToken);
                var specificationContentsOption = await findSpecificationContents(name, cancellationToken);
                var dto = await tryGetDto(name, informationFileDto, specificationContentsOption, cancellationToken);
                var graphQlSpecificationContentsOption = specificationContentsOption.Bind(specificationContents =>
                {
                    var (specification, contents) = specificationContents;

                    return specification is ApiSpecification.GraphQl graphQl
                            ? (graphQl, contents)
                            : Option<(ApiSpecification.GraphQl, BinaryData)>.None;
                });
                await putInApim(name, dto, graphQlSpecificationContentsOption, cancellationToken);
            });
        }

        async ValueTask putCurrentRevision(ApiName name, ApiDto dto, CancellationToken cancellationToken)
        {
            if (ApiName.IsRevisioned(name))
            {
                var rootName = ApiName.GetRootName(name);
                await putApi(rootName, cancellationToken);
            }
            else
            {
                await correctRevisionNumber(name, dto, cancellationToken);
            }
        }

        async ValueTask<ApiDto> tryGetDto(ApiName name,
                                          ApiDto informationFileDto,
                                          Option<(ApiSpecification, BinaryData)> specificationContentsOption,
                                          CancellationToken cancellationToken)
        {
            var dto = informationFileDto;

            await specificationContentsOption.IterTask(async specificationContents =>
            {
                var (specification, contents) = specificationContents;
                dto = await addSpecificationToDto(name, dto, specification, contents, cancellationToken);
            });

            dto = overrideDto(name, dto);

            return dto;
        }

        static async ValueTask<ApiDto> addSpecificationToDto(ApiName name, ApiDto dto, ApiSpecification specification, BinaryData contents, CancellationToken cancellationToken) =>
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
                            await convertStreamToOpenApiV3Yaml(contents, $"Could not convert specification for API {name} to OpenAPIV3.", cancellationToken),
                        _ => contents.ToString()
                    }
                }
            };

        static async ValueTask<string> convertStreamToOpenApiV3Yaml(BinaryData contents, string errorMessage, CancellationToken cancellationToken)
        {
            using var stream = contents.ToStream();
            var readResult = await new OpenApiStreamReader().ReadAsync(stream, cancellationToken);

            return readResult.OpenApiDiagnostic.Errors switch
            {
            [] => readResult.OpenApiDocument.Serialize(OpenApiSpecVersion.OpenApi3_0, Microsoft.OpenApi.OpenApiFormat.Yaml),
                var errors => throw openApiErrorsToException(errorMessage, errors)
            };
        }

        static OpenApiException openApiErrorsToException(string message, IEnumerable<OpenApiError> errors) =>
            new($"{message}. Errors are: {Environment.NewLine}{string.Join(Environment.NewLine, errors)}");
    }

    private static void ConfigureFindApiSpecificationContents(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);
        CommonModule.ConfigureTryGetFileContents(builder);
        CommonModule.ConfigureGetArtifactFiles(builder);

        builder.Services.TryAddSingleton(GetFindApiSpecificationContents);
    }

    private static FindApiSpecificationContents GetFindApiSpecificationContents(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var tryGetFileContents = provider.GetRequiredService<TryGetFileContents>();
        var getArtifactFiles = provider.GetRequiredService<GetArtifactFiles>();

        return async (name, cancellationToken) =>
            await getSpecificationFiles(name)
                    .ToAsyncEnumerable()
                    .Choose(async file => await tryGetSpecificationContentsFromFile(file, cancellationToken))
                    .FirstOrNone(cancellationToken);

        FrozenSet<FileInfo> getSpecificationFiles(ApiName name)
        {
            var apiDirectory = ApiDirectory.From(name, serviceDirectory);
            var artifactFiles = getArtifactFiles();

            return Common.SpecificationFileNames
                         .Select(apiDirectory.ToDirectoryInfo().GetChildFile)
                         .Where(artifactFiles.Contains)
                         .ToFrozenSet();
        }

        async ValueTask<Option<(ApiSpecification, BinaryData)>> tryGetSpecificationContentsFromFile(FileInfo file, CancellationToken cancellationToken)
        {
            var contentsOption = await tryGetFileContents(file, cancellationToken);

            return await contentsOption.BindTask(async contents =>
            {
                var specificationFileOption = await tryParseSpecificationFile(file, contents, cancellationToken);

                return from specificationFile in specificationFileOption
                       select (specificationFile.Specification, contents);
            });
        }

        async ValueTask<Option<ApiSpecificationFile>> tryParseSpecificationFile(FileInfo file, BinaryData contents, CancellationToken cancellationToken) =>
            await ApiSpecificationFile.TryParse(file,
                                                getFileContents: _ => ValueTask.FromResult(contents),
                                                serviceDirectory,
                                                cancellationToken);
    }

    private static void ConfigureFindApiInformationFileDto(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);
        CommonModule.ConfigureTryGetFileContents(builder);

        builder.Services.TryAddSingleton(GetFindApiInformationFileDto);
    }

    private static FindApiInformationFileDto GetFindApiInformationFileDto(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var tryGetFileContents = provider.GetRequiredService<TryGetFileContents>();

        return async (name, cancellationToken) =>
        {
            var informationFile = ApiInformationFile.From(name, serviceDirectory);
            var contentsOption = await tryGetFileContents(informationFile.ToFileInfo(), cancellationToken);

            return from contents in contentsOption
                   select contents.ToObjectFromJson<ApiDto>();
        };
    }

    private static void ConfigureCorrectApimRevisionNumber(IHostApplicationBuilder builder)
    {
        ConfigureGetApiDtosInPreviousCommit(builder);
        ConfigurePutApiInApim(builder);
        ConfigureMakeApiRevisionCurrent(builder);
        ConfigureIsApiNameInSourceControl(builder);
        ConfigureDeleteApiFromApim(builder);

        builder.Services.TryAddSingleton(GetCorrectApimRevisionNumber);
    }

    private static CorrectApimRevisionNumber GetCorrectApimRevisionNumber(IServiceProvider provider)
    {
        var getPreviousCommitDtos = provider.GetRequiredService<GetApiDtosInPreviousCommit>();
        var putApiInApim = provider.GetRequiredService<PutApiInApim>();
        var makeApiRevisionCurrent = provider.GetRequiredService<MakeApiRevisionCurrent>();
        var isNameInSourceControl = provider.GetRequiredService<IsApiNameInSourceControl>();
        var deleteApiFromApim = provider.GetRequiredService<DeleteApiFromApim>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, cancellationToken) =>
        {
            if (ApiName.IsRevisioned(name))
            {
                return;
            }

            var previousRevisionNumberOption = await tryGetPreviousRevisionNumber(name, cancellationToken);
            await previousRevisionNumberOption.IterTask(async previousRevisionNumber =>
            {
                var currentRevisionNumber = Common.GetRevisionNumber(dto);
                await setApimCurrentRevisionNumber(name, currentRevisionNumber, previousRevisionNumber, cancellationToken);
            });
        };

        async ValueTask<Option<ApiRevisionNumber>> tryGetPreviousRevisionNumber(ApiName name, CancellationToken cancellationToken) =>
            await getPreviousCommitDtos()
                    .Find(name)
                    .BindTask(async getDto =>
                    {
                        var dtoOption = await getDto(cancellationToken);

                        return from dto in dtoOption
                               select Common.GetRevisionNumber(dto);
                    });

        async ValueTask setApimCurrentRevisionNumber(ApiName name, ApiRevisionNumber newRevisionNumber, ApiRevisionNumber existingRevisionNumber, CancellationToken cancellationToken)
        {
            if (newRevisionNumber == existingRevisionNumber)
            {
                return;
            }

            logger.LogInformation("Changing current revision on {ApiName} from {RevisionNumber} to {RevisionNumber}...", name, existingRevisionNumber, newRevisionNumber);

            await putRevision(name, newRevisionNumber, existingRevisionNumber, cancellationToken);
            await makeApiRevisionCurrent(name, newRevisionNumber, cancellationToken);
            await deleteOldRevision(name, existingRevisionNumber, cancellationToken);
        }

        async ValueTask putRevision(ApiName name, ApiRevisionNumber revisionNumber, ApiRevisionNumber existingRevisionNumber, CancellationToken cancellationToken)
        {
            var dto = new ApiDto
            {
                Properties = new ApiDto.ApiCreateOrUpdateProperties
                {
                    ApiRevision = revisionNumber.ToString(),
                    SourceApiId = $"/apis/{ApiName.GetRevisionedName(name, existingRevisionNumber)}"
                }
            };

            await putApiInApim(name, dto, Option<(ApiSpecification.GraphQl, BinaryData)>.None, cancellationToken);
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
        async ValueTask deleteOldRevision(ApiName name, ApiRevisionNumber oldRevisionNumber, CancellationToken cancellationToken)
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

    private static void ConfigureGetApiDtosInPreviousCommit(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetArtifactsInPreviousCommit(builder);
        AzureModule.ConfigureManagementServiceDirectory(builder);
        builder.Services.AddMemoryCache();

        builder.Services.TryAddSingleton(GetApiDtosInPreviousCommit);
    }

    private static GetApiDtosInPreviousCommit GetApiDtosInPreviousCommit(IServiceProvider provider)
    {
        var getArtifactsInPreviousCommit = provider.GetRequiredService<GetArtifactsInPreviousCommit>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var cache = provider.GetRequiredService<IMemoryCache>();

        var cacheKey = Guid.NewGuid().ToString();

        return () =>
            cache.GetOrCreate(cacheKey, _ => getDtos())!;

        FrozenDictionary<ApiName, Func<CancellationToken, ValueTask<Option<ApiDto>>>> getDtos() =>
            getArtifactsInPreviousCommit()
                .Choose(kvp => from apiName in tryGetNameFromInformationFile(kvp.Key)
                               select (apiName, tryGetDto(kvp.Value)))
                .ToFrozenDictionary();

        Option<ApiName> tryGetNameFromInformationFile(FileInfo file) =>
            from informationFile in ApiInformationFile.TryParse(file, serviceDirectory)
            select informationFile.Parent.Name;

        static Func<CancellationToken, ValueTask<Option<ApiDto>>> tryGetDto(Func<CancellationToken, ValueTask<Option<BinaryData>>> tryGetContents) =>
            async cancellationToken =>
            {
                var contentsOption = await tryGetContents(cancellationToken);

                return from contents in contentsOption
                       select contents.ToObjectFromJson<ApiDto>();
            };
    }

    private static void ConfigureMakeApiRevisionCurrent(IHostApplicationBuilder builder)
    {
        ApiReleaseModule.ConfigurePutApiReleaseInApim(builder);
        ApiReleaseModule.ConfigureDeleteApiReleaseFromApim(builder);

        builder.Services.TryAddSingleton(GetMakeApiRevisionCurrent);
    }

    private static MakeApiRevisionCurrent GetMakeApiRevisionCurrent(IServiceProvider provider)
    {
        var putRelease = provider.GetRequiredService<PutApiReleaseInApim>();
        var deleteRelease = provider.GetRequiredService<DeleteApiReleaseFromApim>();

        return async (name, revisionNumber, cancellationToken) =>
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
        };
    }

    private static void ConfigurePutApiInApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutApiInApim);
    }

    private static PutApiInApim GetPutApiInApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, graphQlSpecificationContentsOption, cancellationToken) =>
        {
            logger.LogInformation("Putting API {ApiName}...", name);

            var revisionNumber = Common.GetRevisionNumber(dto);
            var uri = getRevisionedUri(name, revisionNumber);

            // APIM sometimes fails revisions if isCurrent is set to true.
            var dtoWithoutIsCurrent = dto with { Properties = dto.Properties with { IsCurrent = null } };

            await uri.PutDto(dtoWithoutIsCurrent, pipeline, cancellationToken);

            // Put GraphQl schema
            await graphQlSpecificationContentsOption.IterTask(async graphQlSpecificationContents =>
            {
                var (_, contents) = graphQlSpecificationContents;
                await uri.PutGraphQlSchema(contents, pipeline, cancellationToken);
            });
        };

        ApiUri getRevisionedUri(ApiName name, ApiRevisionNumber revisionNumber)
        {
            var revisionedApiName = ApiName.GetRevisionedName(name, revisionNumber);
            return ApiUri.From(revisionedApiName, serviceUri);
        }
    }

    public static void ConfigureDeleteApis(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseApiName(builder);
        ConfigureIsApiNameInSourceControl(builder);
        ConfigureDeleteApi(builder);

        builder.Services.TryAddSingleton(GetDeleteApis);
    }

    private static DeleteApis GetDeleteApis(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseApiName>();
        var isNameInSourceControl = provider.GetRequiredService<IsApiNameInSourceControl>();
        var delete = provider.GetRequiredService<DeleteApi>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteApis));

            logger.LogInformation("Deleting APIs...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(name => isNameInSourceControl(name) is false)
                    .Distinct()
                    .IterParallel(delete.Invoke, cancellationToken);
        };
    }

    private static void ConfigureDeleteApi(IHostApplicationBuilder builder)
    {
        ConfigureFindApiInformationFileDto(builder);
        ConfigureDeleteApiFromApim(builder);

        builder.Services.TryAddSingleton(GetDeleteApi);
    }

    private static DeleteApi GetDeleteApi(IServiceProvider provider)
    {
        var findDto = provider.GetRequiredService<FindApiInformationFileDto>();
        var deleteFromApim = provider.GetRequiredService<DeleteApiFromApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        var taskDictionary = new ConcurrentDictionary<ApiName, AsyncLazy<Unit>>();

        return deleteApi;

        async ValueTask deleteApi(ApiName name, CancellationToken cancellationToken)
        {
            using var _ = activitySource.StartActivity(nameof(DeleteApi))
                                       ?.AddTag("api.name", name);

            await taskDictionary.GetOrAdd(name,
                                          name => new AsyncLazy<Unit>(async cancellationToken =>
                                          {
                                              await deleteApiInner(name, cancellationToken);
                                              return Unit.Default;
                                          }))
                                .WithCancellation(cancellationToken);
        };

        async ValueTask deleteApiInner(ApiName name, CancellationToken cancellationToken) =>
            await ApiName.TryParseRevisionedName(name)
                         .Map(async api => await processRevisionedApi(api.RootName, api.RevisionNumber, cancellationToken))
                         .IfLeft(async _ => await processRootApi(name, cancellationToken));

        async ValueTask processRootApi(ApiName name, CancellationToken cancellationToken) =>
            await deleteFromApim(name, cancellationToken);

        async ValueTask processRevisionedApi(ApiName name, ApiRevisionNumber revisionNumber, CancellationToken cancellationToken)
        {
            var rootName = ApiName.GetRootName(name);
            var currentRevisionNumberOption = await tryGetRevisionNumberInSourceControl(rootName, cancellationToken);

            await currentRevisionNumberOption.Match(// If the current revision in source control has a different revision number, delete this revision.
                                                    // We don't want to delete a revision if it was just made current. For instance:
                                                    // 1. Dev has apiA  with revision 1 (current) and revision 2. Artifacts folder has:
                                                    //     - /apis/apiA/apiInformation.json with revision 1 as current
                                                    //     - /apis/apiA;rev=2/apiInformation.json
                                                    // 2. User makes revision 2 current in dev APIM.
                                                    // 3. User runs extractor for dev APIM. Artifacts folder has:
                                                    //     - /apis/apiA/apiInformation.json with revision 2 as current
                                                    //     - /apis/apiA;rev=1/apiInformation.json
                                                    //     - /apis/apiA;rev=2 folder gets deleted.
                                                    // 4. User runs publisher to prod APIM. We don't want to handle the deletion of folder /apis/apiA;rev=2, as it's the current revision.
                                                    async currentRevisionNumber =>
                                                    {
                                                        if (currentRevisionNumber != revisionNumber)
                                                        {
                                                            await deleteFromApim(name, cancellationToken);
                                                        }
                                                    },
                                                    // If there is no current revision in source control, process the root API deletion
                                                    async () => await deleteApi(rootName, cancellationToken));
        }

        async ValueTask<Option<ApiRevisionNumber>> tryGetRevisionNumberInSourceControl(ApiName name, CancellationToken cancellationToken)
        {
            var dtoOption = await findDto(name, cancellationToken);

            return dtoOption.Map(Common.GetRevisionNumber);
        }
    }

    private static void ConfigureDeleteApiFromApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteApiFromApim);
    }

    private static DeleteApiFromApim GetDeleteApiFromApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, cancellationToken) =>
        {
            logger.LogInformation("Deleting API {ApiName}...", name);

            var apiUri = ApiUri.From(name, serviceUri);

            await (ApiName.IsRevisioned(name)
                    ? apiUri.Delete(pipeline, cancellationToken)
                    : apiUri.DeleteAllRevisions(pipeline, cancellationToken));
        };
    }
}

file static class Common
{
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