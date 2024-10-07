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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace publisher;

public delegate ValueTask PutWorkspaceApis(CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceApis(CancellationToken cancellationToken);
public delegate Option<(ApiName Name, WorkspaceName WorkspaceName)> TryParseWorkspaceApiName(FileInfo file);
public delegate bool IsWorkspaceApiNameInSourceControl(ApiName name, WorkspaceName workspaceName);
public delegate ValueTask PutWorkspaceApi(ApiName name, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask<Option<WorkspaceApiDto>> FindWorkspaceApiDto(ApiName name, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask<Option<(ApiSpecification Specification, BinaryData Contents)>> FindWorkspaceApiSpecificationContents(ApiName name, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask CorrectWorkspaceApimRevisionNumber(ApiName name, WorkspaceApiDto Dto, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate FrozenDictionary<(ApiName, WorkspaceName), Func<CancellationToken, ValueTask<Option<WorkspaceApiDto>>>> GetWorkspaceApiDtosInPreviousCommit();
public delegate ValueTask MakeWorkspaceApiRevisionCurrent(ApiName name, ApiRevisionNumber revisionNumber, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask PutWorkspaceApiInApim(ApiName name, WorkspaceApiDto dto, Option<(ApiSpecification.GraphQl Specification, BinaryData Contents)> graphQlSpecificationContentsOption, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceApi(ApiName name, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceApiFromApim(ApiName name, WorkspaceName workspaceName, CancellationToken cancellationToken);

internal static class WorkspaceApiModule
{
    public static void ConfigurePutWorkspaceApis(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseWorkspaceApiName(builder);
        ConfigureIsWorkspaceApiNameInSourceControl(builder);
        ConfigurePutWorkspaceApi(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceApis);
    }

    private static PutWorkspaceApis GetPutWorkspaceApis(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseWorkspaceApiName>();
        var isNameInSourceControl = provider.GetRequiredService<IsWorkspaceApiNameInSourceControl>();
        var put = provider.GetRequiredService<PutWorkspaceApi>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(PutWorkspaceApis));

            logger.LogInformation("Putting workspace APIs...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(resource => isNameInSourceControl(resource.Name, resource.WorkspaceName))
                    .Distinct()
                    .IterParallel(put.Invoke, cancellationToken);
        };
    }

    private static void ConfigureTryParseWorkspaceApiName(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetTryParseWorkspaceApiName);
    }

    private static TryParseWorkspaceApiName GetTryParseWorkspaceApiName(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return file => from informationFile in WorkspaceApiInformationFile.TryParse(file, serviceDirectory)
                       select (informationFile.Parent.Name, informationFile.Parent.Parent.Parent.Name);
    }

    private static void ConfigureIsWorkspaceApiNameInSourceControl(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetArtifactFiles(builder);
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetIsWorkspaceApiNameInSourceControl);
    }

    private static IsWorkspaceApiNameInSourceControl GetIsWorkspaceApiNameInSourceControl(IServiceProvider provider)
    {
        var getArtifactFiles = provider.GetRequiredService<GetArtifactFiles>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return (name, workspaceName) =>
            doesInformationFileExist(name, workspaceName)
            || doesSpecificationFileExist(name, workspaceName);

        bool doesInformationFileExist(ApiName name, WorkspaceName workspaceName)
        {
            var artifactFiles = getArtifactFiles();
            var informationFile = WorkspaceApiInformationFile.From(name, workspaceName, serviceDirectory);

            return artifactFiles.Contains(informationFile.ToFileInfo());
        }

        bool doesSpecificationFileExist(ApiName name, WorkspaceName workspaceName)
        {
            var artifactFiles = getArtifactFiles();
            var getFileInApiDirectory = WorkspaceApiDirectory.From(name, workspaceName, serviceDirectory)
                                                             .ToDirectoryInfo()
                                                             .GetChildFile;

            return Common.SpecificationFileNames
                         .Select(getFileInApiDirectory)
                         .Any(artifactFiles.Contains);
        }
    }

    private static void ConfigurePutWorkspaceApi(IHostApplicationBuilder builder)
    {
        ConfigureFindWorkspaceApiDto(builder);
        ConfigureFindWorkspaceApiSpecificationContents(builder);
        ConfigureCorrectWorkspaceApimRevisionNumber(builder);
        ConfigurePutWorkspaceApiInApim(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceApi);
    }

    private static PutWorkspaceApi GetPutWorkspaceApi(IServiceProvider provider)
    {
        var findDto = provider.GetRequiredService<FindWorkspaceApiDto>();
        var findSpecificationContents = provider.GetRequiredService<FindWorkspaceApiSpecificationContents>();
        var correctRevisionNumber = provider.GetRequiredService<CorrectWorkspaceApimRevisionNumber>();
        var putInApim = provider.GetRequiredService<PutWorkspaceApiInApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        var taskDictionary = new ConcurrentDictionary<(ApiName, WorkspaceName), AsyncLazy<Unit>>();

        return putApi;

        async ValueTask putApi(ApiName name, WorkspaceName workspaceName, CancellationToken cancellationToken)
        {
            using var _ = activitySource.StartActivity(nameof(PutWorkspaceApi))
                                       ?.AddTag("workspace.name", workspaceName)
                                       ?.AddTag("workspace_api.name", name);

            await taskDictionary.GetOrAdd((name, workspaceName),
                                          (pair) => new AsyncLazy<Unit>(async cancellationToken =>
                                          {
                                              var (name, workspaceName) = pair;
                                              await putApiInner(name, workspaceName, cancellationToken);
                                              return Unit.Default;
                                          }))
                                .WithCancellation(cancellationToken);
        };

        async ValueTask putApiInner(ApiName name, WorkspaceName workspaceName, CancellationToken cancellationToken)
        {
            var informationFileDtoOption = await findDto(name, workspaceName, cancellationToken);
            await informationFileDtoOption.IterTask(async informationFileDto =>
            {
                await putCurrentRevision(name, informationFileDto, workspaceName, cancellationToken);
                var specificationContentsOption = await findSpecificationContents(name, workspaceName, cancellationToken);
                var dto = await tryGetDto(name, informationFileDto, specificationContentsOption, cancellationToken);
                var graphQlSpecificationContentsOption = specificationContentsOption.Bind(specificationContents =>
                {
                    var (specification, contents) = specificationContents;

                    return specification is ApiSpecification.GraphQl graphQl
                            ? (graphQl, contents)
                            : Option<(ApiSpecification.GraphQl, BinaryData)>.None;
                });
                await putInApim(name, dto, graphQlSpecificationContentsOption, workspaceName, cancellationToken);
            });
        }

        async ValueTask putCurrentRevision(ApiName name, WorkspaceApiDto dto, WorkspaceName workspaceName, CancellationToken cancellationToken)
        {
            if (ApiName.IsRevisioned(name))
            {
                var rootName = ApiName.GetRootName(name);
                await putApi(rootName, workspaceName, cancellationToken);
            }
            else
            {
                await correctRevisionNumber(name, dto, workspaceName, cancellationToken);
            }
        }

        async ValueTask<WorkspaceApiDto> tryGetDto(ApiName name,
                                                   WorkspaceApiDto informationFileDto,
                                                   Option<(ApiSpecification, BinaryData)> specificationContentsOption,
                                                   CancellationToken cancellationToken)
        {
            var dto = informationFileDto;

            await specificationContentsOption.IterTask(async specificationContents =>
            {
                var (specification, contents) = specificationContents;
                dto = await addSpecificationToDto(name, dto, specification, contents, cancellationToken);
            });

            return dto;
        }

        static async ValueTask<WorkspaceApiDto> addSpecificationToDto(ApiName name, WorkspaceApiDto dto, ApiSpecification specification, BinaryData contents, CancellationToken cancellationToken) =>
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

    private static void ConfigureFindWorkspaceApiDto(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);
        CommonModule.ConfigureTryGetFileContents(builder);

        builder.Services.TryAddSingleton(GetFindWorkspaceApiDto);
    }

    private static FindWorkspaceApiDto GetFindWorkspaceApiDto(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var tryGetFileContents = provider.GetRequiredService<TryGetFileContents>();

        return async (name, workspaceName, cancellationToken) =>
        {
            var informationFile = WorkspaceApiInformationFile.From(name, workspaceName, serviceDirectory);
            var contentsOption = await tryGetFileContents(informationFile.ToFileInfo(), cancellationToken);

            return from contents in contentsOption
                   select contents.ToObjectFromJson<WorkspaceApiDto>();
        };
    }

    private static void ConfigureFindWorkspaceApiSpecificationContents(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);
        CommonModule.ConfigureTryGetFileContents(builder);
        CommonModule.ConfigureGetArtifactFiles(builder);

        builder.Services.TryAddSingleton(GetFindWorkspaceApiSpecificationContents);
    }

    private static FindWorkspaceApiSpecificationContents GetFindWorkspaceApiSpecificationContents(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var tryGetFileContents = provider.GetRequiredService<TryGetFileContents>();
        var getArtifactFiles = provider.GetRequiredService<GetArtifactFiles>();

        return async (name, workspaceName, cancellationToken) =>
            await getSpecificationFiles(name, workspaceName)
                    .ToAsyncEnumerable()
                    .Choose(async file => await tryGetSpecificationContentsFromFile(file, cancellationToken))
                    .FirstOrNone(cancellationToken);

        FrozenSet<FileInfo> getSpecificationFiles(ApiName name, WorkspaceName workspaceName)
        {
            var apiDirectory = WorkspaceApiDirectory.From(name, workspaceName, serviceDirectory);
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

    private static void ConfigureCorrectWorkspaceApimRevisionNumber(IHostApplicationBuilder builder)
    {
        ConfigureGetWorkspaceApiDtosInPreviousCommit(builder);
        ConfigurePutWorkspaceApiInApim(builder);
        ConfigureMakeWorkspaceApiRevisionCurrent(builder);
        ConfigureIsWorkspaceApiNameInSourceControl(builder);
        ConfigureDeleteWorkspaceApiFromApim(builder);

        builder.Services.TryAddSingleton(GetCorrectWorkspaceApimRevisionNumber);
    }

    private static CorrectWorkspaceApimRevisionNumber GetCorrectWorkspaceApimRevisionNumber(IServiceProvider provider)
    {
        var getPreviousCommitDtos = provider.GetRequiredService<GetWorkspaceApiDtosInPreviousCommit>();
        var putApiInApim = provider.GetRequiredService<PutWorkspaceApiInApim>();
        var makeApiRevisionCurrent = provider.GetRequiredService<MakeWorkspaceApiRevisionCurrent>();
        var isNameInSourceControl = provider.GetRequiredService<IsWorkspaceApiNameInSourceControl>();
        var deleteApiFromApim = provider.GetRequiredService<DeleteWorkspaceApiFromApim>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, workspaceName, cancellationToken) =>
        {
            if (ApiName.IsRevisioned(name))
            {
                return;
            }

            var previousRevisionNumberOption = await tryGetPreviousRevisionNumber(name, workspaceName, cancellationToken);
            await previousRevisionNumberOption.IterTask(async previousRevisionNumber =>
            {
                var currentRevisionNumber = Common.GetRevisionNumber(dto);
                await setApimCurrentRevisionNumber(name, currentRevisionNumber, previousRevisionNumber, workspaceName, cancellationToken);
            });
        };

        async ValueTask<Option<ApiRevisionNumber>> tryGetPreviousRevisionNumber(ApiName name, WorkspaceName workspaceName, CancellationToken cancellationToken) =>
            await getPreviousCommitDtos()
                    .Find((name, workspaceName))
                    .BindTask(async getDto =>
                    {
                        var dtoOption = await getDto(cancellationToken);

                        return from dto in dtoOption
                               select Common.GetRevisionNumber(dto);
                    });

        async ValueTask setApimCurrentRevisionNumber(ApiName name, ApiRevisionNumber newRevisionNumber, ApiRevisionNumber existingRevisionNumber, WorkspaceName workspaceName, CancellationToken cancellationToken)
        {
            if (newRevisionNumber == existingRevisionNumber)
            {
                return;
            }

            logger.LogInformation("Changing current revision on {ApiName} in workspace {WorkspaceName} from {RevisionNumber} to {RevisionNumber}...", name, workspaceName, existingRevisionNumber, newRevisionNumber);

            await putRevision(name, newRevisionNumber, existingRevisionNumber, workspaceName, cancellationToken);
            await makeApiRevisionCurrent(name, newRevisionNumber, workspaceName, cancellationToken);
            await deleteOldRevision(name, existingRevisionNumber, workspaceName, cancellationToken);
        }

        async ValueTask putRevision(ApiName name, ApiRevisionNumber revisionNumber, ApiRevisionNumber existingRevisionNumber, WorkspaceName workspaceName, CancellationToken cancellationToken)
        {
            var dto = new WorkspaceApiDto
            {
                Properties = new WorkspaceApiDto.ApiCreateOrUpdateProperties
                {
                    ApiRevision = revisionNumber.ToString(),
                    SourceApiId = $"/apis/{ApiName.GetRevisionedName(name, existingRevisionNumber)}"
                }
            };

            await putApiInApim(name, dto, Option<(ApiSpecification.GraphQl, BinaryData)>.None, workspaceName, cancellationToken);
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
        async ValueTask deleteOldRevision(ApiName name, ApiRevisionNumber oldRevisionNumber, WorkspaceName workspaceName, CancellationToken cancellationToken)
        {
            var revisionedName = ApiName.GetRevisionedName(name, oldRevisionNumber);

            if (isNameInSourceControl(revisionedName, workspaceName))
            {
                return;
            }

            logger.LogInformation("Deleting old revision {RevisionNumber} of {ApiName} in workspace {WorkspaceName}...", oldRevisionNumber, name, workspaceName);
            await deleteApiFromApim(revisionedName, workspaceName, cancellationToken);
        }
    }

    private static void ConfigureGetWorkspaceApiDtosInPreviousCommit(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetArtifactsInPreviousCommit(builder);
        AzureModule.ConfigureManagementServiceDirectory(builder);
        builder.Services.AddMemoryCache();

        builder.Services.TryAddSingleton(GetWorkspaceApiDtosInPreviousCommit);
    }

    private static GetWorkspaceApiDtosInPreviousCommit GetWorkspaceApiDtosInPreviousCommit(IServiceProvider provider)
    {
        var getArtifactsInPreviousCommit = provider.GetRequiredService<GetArtifactsInPreviousCommit>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var cache = provider.GetRequiredService<IMemoryCache>();

        var cacheKey = Guid.NewGuid().ToString();

        return () =>
            cache.GetOrCreate(cacheKey, _ => getDtos())!;

        FrozenDictionary<(ApiName, WorkspaceName), Func<CancellationToken, ValueTask<Option<WorkspaceApiDto>>>> getDtos() =>
            getArtifactsInPreviousCommit()
                .Choose(kvp => from apiName in tryGetNameFromInformationFile(kvp.Key)
                               select (apiName, tryGetDto(kvp.Value)))
                .ToFrozenDictionary();

        Option<(ApiName, WorkspaceName)> tryGetNameFromInformationFile(FileInfo file) =>
            from informationFile in WorkspaceApiInformationFile.TryParse(file, serviceDirectory)
            select (informationFile.Parent.Name, informationFile.Parent.Parent.Parent.Name);

        static Func<CancellationToken, ValueTask<Option<WorkspaceApiDto>>> tryGetDto(Func<CancellationToken, ValueTask<Option<BinaryData>>> tryGetContents) =>
            async cancellationToken =>
            {
                var contentsOption = await tryGetContents(cancellationToken);

                return from contents in contentsOption
                       select contents.ToObjectFromJson<WorkspaceApiDto>();
            };
    }

    private static void ConfigureMakeWorkspaceApiRevisionCurrent(IHostApplicationBuilder builder)
    {
        WorkspaceApiReleaseModule.ConfigurePutWorkspaceApiReleaseInApim(builder);
        WorkspaceApiReleaseModule.ConfigureDeleteWorkspaceApiReleaseFromApim(builder);

        builder.Services.TryAddSingleton(GetMakeWorkspaceApiRevisionCurrent);
    }

    private static MakeWorkspaceApiRevisionCurrent GetMakeWorkspaceApiRevisionCurrent(IServiceProvider provider)
    {
        var putRelease = provider.GetRequiredService<PutWorkspaceApiReleaseInApim>();
        var deleteRelease = provider.GetRequiredService<DeleteWorkspaceApiReleaseFromApim>();

        return async (name, revisionNumber, workspaceName, cancellationToken) =>
        {
            var revisionedName = ApiName.GetRevisionedName(name, revisionNumber);
            var releaseName = WorkspaceApiReleaseName.From("apiops-set-current");
            var releaseDto = new WorkspaceApiReleaseDto
            {
                Properties = new WorkspaceApiReleaseDto.ApiReleaseContract
                {
                    ApiId = $"/apis/{revisionedName}",
                    Notes = "Setting current revision for ApiOps"
                }
            };

            await putRelease(releaseName, releaseDto, name, workspaceName, cancellationToken);
            await deleteRelease(releaseName, name, workspaceName, cancellationToken);
        };
    }

    private static void ConfigurePutWorkspaceApiInApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceApiInApim);
    }

    private static PutWorkspaceApiInApim GetPutWorkspaceApiInApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, graphQlSpecificationContentsOption, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Adding API {ApiName} to workspace {WorkspaceName}...", name, workspaceName);

            var revisionNumber = Common.GetRevisionNumber(dto);
            var uri = getRevisionedUri(name, workspaceName, revisionNumber);

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

        WorkspaceApiUri getRevisionedUri(ApiName name, WorkspaceName workspaceName, ApiRevisionNumber revisionNumber)
        {
            var revisionedApiName = ApiName.GetRevisionedName(name, revisionNumber);
            return WorkspaceApiUri.From(revisionedApiName, workspaceName, serviceUri);
        }
    }

    public static void ConfigureDeleteWorkspaceApis(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseWorkspaceApiName(builder);
        ConfigureIsWorkspaceApiNameInSourceControl(builder);
        ConfigureDeleteWorkspaceApi(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceApis);
    }

    private static DeleteWorkspaceApis GetDeleteWorkspaceApis(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseWorkspaceApiName>();
        var isNameInSourceControl = provider.GetRequiredService<IsWorkspaceApiNameInSourceControl>();
        var delete = provider.GetRequiredService<DeleteWorkspaceApi>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteWorkspaceApis));

            logger.LogInformation("Deleting workspace APIs...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(resource => isNameInSourceControl(resource.Name, resource.WorkspaceName) is false)
                    .Distinct()
                    .IterParallel(delete.Invoke, cancellationToken);
        };
    }

    private static void ConfigureDeleteWorkspaceApi(IHostApplicationBuilder builder)
    {
        ConfigureFindWorkspaceApiDto(builder);
        ConfigureDeleteWorkspaceApiFromApim(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceApi);
    }

    private static DeleteWorkspaceApi GetDeleteWorkspaceApi(IServiceProvider provider)
    {
        var findDto = provider.GetRequiredService<FindWorkspaceApiDto>();
        var deleteFromApim = provider.GetRequiredService<DeleteWorkspaceApiFromApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        var taskDictionary = new ConcurrentDictionary<(ApiName, WorkspaceName), AsyncLazy<Unit>>();

        return async (name, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteWorkspaceApi))
                                       ?.AddTag("workspace.name", workspaceName)
                                       ?.AddTag("workspace_api.name", name);

            await taskDictionary.GetOrAdd((name, workspaceName),
                                          pair => new AsyncLazy<Unit>(async cancellationToken =>
                                          {
                                              var (name, workspaceName) = pair;
                                              await deleteApiInner(name, workspaceName, cancellationToken);
                                              return Unit.Default;
                                          }))
                                .WithCancellation(cancellationToken);
        };

        async ValueTask deleteApiInner(ApiName name, WorkspaceName workspaceName, CancellationToken cancellationToken)
        {
            if (await isApiRevisionNumberCurrentInSourceControl(name, workspaceName, cancellationToken))
            {
                logger.LogInformation("API {ApiName} in workspace {WorkspaceName} is the current revision in source control. Skipping deletion...", name, workspaceName);
                return;
            }

            await deleteFromApim(name, workspaceName, cancellationToken);
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
        async ValueTask<bool> isApiRevisionNumberCurrentInSourceControl(ApiName name, WorkspaceName workspaceName, CancellationToken cancellationToken) =>
            await ApiName.TryParseRevisionedName(name)
                         .Match(async _ => await ValueTask.FromResult(false),
                                async api =>
                                {
                                    var (rootName, revisionNumber) = api;
                                    var sourceControlRevisionNumberOption = await tryGetRevisionNumberInSourceControl(rootName, workspaceName, cancellationToken);
                                    return sourceControlRevisionNumberOption
                                           .Map(sourceControlRevisionNumber => sourceControlRevisionNumber == revisionNumber)
                                           .IfNone(false);
                                });

        async ValueTask<Option<ApiRevisionNumber>> tryGetRevisionNumberInSourceControl(ApiName name, WorkspaceName workspaceName, CancellationToken cancellationToken)
        {
            var dtoOption = await findDto(name, workspaceName, cancellationToken);

            return dtoOption.Map(Common.GetRevisionNumber);
        }
    }

    private static void ConfigureDeleteWorkspaceApiFromApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceApiFromApim);
    }

    private static DeleteWorkspaceApiFromApim GetDeleteWorkspaceApiFromApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Removing API {ApiName} from workspace {WorkspaceName}...", name, workspaceName);

            var resourceUri = WorkspaceApiUri.From(name, workspaceName, serviceUri);
            await resourceUri.Delete(pipeline, cancellationToken);
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

    public static ApiRevisionNumber GetRevisionNumber(WorkspaceApiDto dto) =>
        ApiRevisionNumber.TryFrom(dto.Properties.ApiRevision)
                         .IfNone(() => ApiRevisionNumber.From(1));
}