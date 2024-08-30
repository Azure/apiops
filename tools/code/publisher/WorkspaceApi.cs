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
public delegate Option<(WorkspaceApiName WorkspaceApiName, WorkspaceName WorkspaceName)> TryParseWorkspaceApiName(FileInfo file);
public delegate bool IsWorkspaceApiNameInSourceControl(WorkspaceApiName workspaceApiName, WorkspaceName workspaceName);
public delegate ValueTask PutWorkspaceApi(WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask<Option<(WorkspaceApiDto Dto, Option<(ApiSpecification.GraphQl Specification, BinaryData Contents)> GraphQlSpecificationContentsOption)>> FindWorkspaceApiDto(WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask<Option<WorkspaceApiDto>> FindWorkspaceApiInformationFileDto(WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask<Option<(ApiSpecification Specification, BinaryData Contents)>> FindWorkspaceApiSpecificationContents(WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask CorrectWorkspaceApimRevisionNumber(WorkspaceApiName name, WorkspaceApiDto Dto, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate FrozenDictionary<(WorkspaceApiName, WorkspaceName), Func<CancellationToken, ValueTask<Option<WorkspaceApiDto>>>> GetWorkspaceApiDtosInPreviousCommit();
public delegate ValueTask MakeWorkspaceApiRevisionCurrent(WorkspaceApiName name, ApiRevisionNumber revisionNumber, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask PutWorkspaceApiInApim(WorkspaceApiName name, WorkspaceApiDto dto, Option<(ApiSpecification.GraphQl Specification, BinaryData Contents)> graphQlSpecificationContentsOption, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceApi(WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceApiFromApim(WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, CancellationToken cancellationToken);

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
                    .Where(resource => isNameInSourceControl(resource.WorkspaceApiName, resource.WorkspaceName))
                    .Distinct()
                    .IterParallel(resource => put(resource.WorkspaceApiName, resource.WorkspaceName, cancellationToken),
                                  cancellationToken);
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

        return file => tryParseNameFromInformationFile(file) | tryParseNameFromSpecificationFile(file);

        Option<(WorkspaceApiName, WorkspaceName)> tryParseNameFromInformationFile(FileInfo file) =>
            from informationFile in WorkspaceApiInformationFile.TryParse(file, serviceDirectory)
            select (informationFile.Parent.Name, informationFile.Parent.Parent.Parent.Name);

        Option<(WorkspaceApiName, WorkspaceName)> tryParseNameFromSpecificationFile(FileInfo file) =>
            from apiDirectory in WorkspaceApiDirectory.TryParse(file.Directory, serviceDirectory)
            where Common.SpecificationFileNames.Contains(file.Name)
            select (apiDirectory.Name, apiDirectory.Parent.Parent.Name);
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

        return (name, workspaceName) => doesInformationFileExist(name, workspaceName) || doesSpecificationFileExist(name, workspaceName);

        bool doesInformationFileExist(WorkspaceApiName workspaceApiName, WorkspaceName workspaceName)
        {
            var artifactFiles = getArtifactFiles();
            var informationFile = WorkspaceApiInformationFile.From(workspaceApiName, workspaceName, serviceDirectory);

            return artifactFiles.Contains(informationFile.ToFileInfo());
        }

        bool doesSpecificationFileExist(WorkspaceApiName workspaceApiName, WorkspaceName workspaceName)
        {
            var artifactFiles = getArtifactFiles();
            var getFileInApiDirectory = WorkspaceApiDirectory.From(workspaceApiName, workspaceName, serviceDirectory)
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
        ConfigureCorrectWorkspaceApimRevisionNumber(builder);
        ConfigurePutWorkspaceApiInApim(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceApi);
    }

    private static PutWorkspaceApi GetPutWorkspaceApi(IServiceProvider provider)
    {
        var findDto = provider.GetRequiredService<FindWorkspaceApiDto>();
        var correctRevisionNumber = provider.GetRequiredService<CorrectWorkspaceApimRevisionNumber>();
        var putInApim = provider.GetRequiredService<PutWorkspaceApiInApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        var taskDictionary = new ConcurrentDictionary<(WorkspaceApiName, WorkspaceName), AsyncLazy<Unit>>();

        return putApi;

        async ValueTask putApi(WorkspaceApiName name, WorkspaceName workspaceName, CancellationToken cancellationToken)
        {
            using var _ = activitySource.StartActivity(nameof(PutWorkspaceApi));

            await taskDictionary.GetOrAdd((name, workspaceName),
                                          x => new AsyncLazy<Unit>(async cancellationToken =>
                                          {
                                              var (name, workspaceName) = x;

                                              var dtoOption = await findDto(name, workspaceName, cancellationToken);
                                              await dtoOption.IterTask(async dto =>
                                              {
                                                  await putCurrentRevision(name, dto.Dto, workspaceName, cancellationToken);
                                                  await putInApim(name, dto.Dto, dto.GraphQlSpecificationContentsOption, workspaceName, cancellationToken);
                                              });

                                              return Unit.Default;
                                          }))
                                .WithCancellation(cancellationToken);
        };

        async ValueTask putCurrentRevision(WorkspaceApiName name, WorkspaceApiDto dto, WorkspaceName workspaceName, CancellationToken cancellationToken)
        {
            if (WorkspaceApiName.IsRevisioned(name))
            {
                var rootName = WorkspaceApiName.GetRootName(name);
                await putApi(rootName, workspaceName, cancellationToken);
            }
            else
            {
                await correctRevisionNumber(name, dto, workspaceName, cancellationToken);
            }
        }
    }

    private static void ConfigureFindWorkspaceApiDto(IHostApplicationBuilder builder)
    {
        ConfigureFindWorkspaceApiInformationFileDto(builder);
        ConfigureFindWorkspaceApiSpecificationContents(builder);
        AzureModule.ConfigureManagementServiceDirectory(builder);
        CommonModule.ConfigureTryGetFileContents(builder);

        builder.Services.TryAddSingleton(GetFindWorkspaceApiDto);
    }

    private static FindWorkspaceApiDto GetFindWorkspaceApiDto(IServiceProvider provider)
    {
        var findInformationFileDto = provider.GetRequiredService<FindWorkspaceApiInformationFileDto>();
        var findSpecificationContents = provider.GetRequiredService<FindWorkspaceApiSpecificationContents>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var tryGetFileContents = provider.GetRequiredService<TryGetFileContents>();

        return async (workspaceApiName, workspaceName, cancellationToken) =>
        {
            var informationFileDtoOption = await findInformationFileDto(workspaceApiName, workspaceName, cancellationToken);

            return await informationFileDtoOption.MapTask(async informationFileDto =>
            {
                var specificationContentsOption = await findSpecificationContents(workspaceApiName, workspaceName, cancellationToken);
                var dto = await tryGetDto(workspaceApiName, informationFileDto, specificationContentsOption, workspaceName, cancellationToken);
                var graphQlSpecificationContentsOption = specificationContentsOption.Bind(specificationContents =>
                {
                    var (specification, contents) = specificationContents;

                    return specification is ApiSpecification.GraphQl graphQl
                            ? (graphQl, contents)
                            : Option<(ApiSpecification.GraphQl, BinaryData)>.None;
                });

                return (dto, graphQlSpecificationContentsOption);
            });
        };

        async ValueTask<WorkspaceApiDto> tryGetDto(WorkspaceApiName name,
                                                   WorkspaceApiDto informationFileDto,
                                                   Option<(ApiSpecification, BinaryData)> specificationContentsOption,
                                                   WorkspaceName workspaceName,
                                                   CancellationToken cancellationToken)
        {
            var dto = informationFileDto;

            await specificationContentsOption.IterTask(async specificationContents =>
            {
                var (specification, contents) = specificationContents;
                dto = await addSpecificationToDto(name, dto, specification, contents, workspaceName, cancellationToken);
            });

            return dto;
        }

        static async ValueTask<WorkspaceApiDto> addSpecificationToDto(WorkspaceApiName name,
                                                                      WorkspaceApiDto dto,
                                                                      ApiSpecification specification,
                                                                      BinaryData contents,
                                                                      WorkspaceName workspaceName,
                                                                      CancellationToken cancellationToken) =>
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
                            await convertStreamToOpenApiV3Yaml(contents, $"Could not convert specification for API {name} in workspace {workspaceName} to OpenAPIV3.", cancellationToken),
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

    private static void ConfigureFindWorkspaceApiInformationFileDto(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);
        CommonModule.ConfigureTryGetFileContents(builder);

        builder.Services.TryAddSingleton(GetFindWorkspaceApiInformationFileDto);
    }

    private static FindWorkspaceApiInformationFileDto GetFindWorkspaceApiInformationFileDto(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var tryGetFileContents = provider.GetRequiredService<TryGetFileContents>();

        return async (workspaceApiName, workspaceName, cancellationToken) =>
        {
            var informationFile = WorkspaceApiInformationFile.From(workspaceApiName, workspaceName, serviceDirectory);
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

        FrozenSet<FileInfo> getSpecificationFiles(WorkspaceApiName name, WorkspaceName workspaceName)
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

        async ValueTask<Option<WorkspaceApiSpecificationFile>> tryParseSpecificationFile(FileInfo file, BinaryData contents, CancellationToken cancellationToken) =>
            await WorkspaceApiSpecificationFile.TryParse(file,
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
            if (WorkspaceApiName.IsRevisioned(name))
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

        async ValueTask<Option<ApiRevisionNumber>> tryGetPreviousRevisionNumber(WorkspaceApiName name, WorkspaceName workspaceName, CancellationToken cancellationToken) =>
            await getPreviousCommitDtos()
                    .Find((name, workspaceName))
                    .BindTask(async getDto =>
                    {
                        var dtoOption = await getDto(cancellationToken);

                        return from dto in dtoOption
                               select Common.GetRevisionNumber(dto);
                    });

        async ValueTask setApimCurrentRevisionNumber(WorkspaceApiName name, ApiRevisionNumber newRevisionNumber, ApiRevisionNumber existingRevisionNumber, WorkspaceName workspaceName, CancellationToken cancellationToken)
        {
            if (newRevisionNumber == existingRevisionNumber)
            {
                return;
            }

            logger.LogInformation("Changing current revision on {ApiName} from {RevisionNumber} to {RevisionNumber}...", name, existingRevisionNumber, newRevisionNumber);

            await putRevision(name, newRevisionNumber, existingRevisionNumber, workspaceName, cancellationToken);
            await makeApiRevisionCurrent(name, newRevisionNumber, workspaceName, cancellationToken);
            await deleteOldRevision(name, existingRevisionNumber, workspaceName, cancellationToken);
        }

        async ValueTask putRevision(WorkspaceApiName name, ApiRevisionNumber revisionNumber, ApiRevisionNumber existingRevisionNumber, WorkspaceName workspaceName, CancellationToken cancellationToken)
        {
            var dto = new WorkspaceApiDto
            {
                Properties = new WorkspaceApiDto.ApiCreateOrUpdateProperties
                {
                    ApiRevision = revisionNumber.ToString(),
                    SourceApiId = $"/apis/{WorkspaceApiName.GetRevisionedName(name, existingRevisionNumber)}"
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
        async ValueTask deleteOldRevision(WorkspaceApiName name, ApiRevisionNumber oldRevisionNumber, WorkspaceName workspaceName, CancellationToken cancellationToken)
        {
            var revisionedName = WorkspaceApiName.GetRevisionedName(name, oldRevisionNumber);

            if (isNameInSourceControl(revisionedName, workspaceName))
            {
                return;
            }

            logger.LogInformation("Deleting old revision {RevisionNumber} of API {WorkspaceApiName} in workspace {WorkspaceName}...", oldRevisionNumber, name, workspaceName);
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

        FrozenDictionary<(WorkspaceApiName, WorkspaceName), Func<CancellationToken, ValueTask<Option<WorkspaceApiDto>>>> getDtos() =>
            getArtifactsInPreviousCommit()
                .Choose(kvp => from apiName in tryGetNameFromInformationFile(kvp.Key)
                               select (apiName, tryGetDto(kvp.Value)))
                .ToFrozenDictionary();

        Option<(WorkspaceApiName, WorkspaceName)> tryGetNameFromInformationFile(FileInfo file) =>
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
            logger.LogInformation("Putting API {WorkspaceApiName} in workspace {WorkspaceName}...", name, workspaceName);

            var revisionNumber = Common.GetRevisionNumber(dto);
            var uri = getRevisionedUri(name, revisionNumber, workspaceName);

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

        WorkspaceApiUri getRevisionedUri(WorkspaceApiName name, ApiRevisionNumber revisionNumber, WorkspaceName workspaceName)
        {
            var revisionedApiName = WorkspaceApiName.GetRevisionedName(name, revisionNumber);
            return WorkspaceApiUri.From(revisionedApiName, workspaceName, serviceUri);
        }
    }

    public static void ConfigureMakeWorkspaceApiRevisionCurrent(IHostApplicationBuilder builder)
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
            var revisionedName = WorkspaceApiName.GetRevisionedName(name, revisionNumber);
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
                    .Where(resource => isNameInSourceControl(resource.WorkspaceApiName, resource.WorkspaceName) is false)
                    .Distinct()
                    .IterParallel(resource => delete(resource.WorkspaceApiName, resource.WorkspaceName, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureDeleteWorkspaceApi(IHostApplicationBuilder builder)
    {
        ConfigureFindWorkspaceApiInformationFileDto(builder);
        ConfigureDeleteWorkspaceApiFromApim(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceApi);
    }

    private static DeleteWorkspaceApi GetDeleteWorkspaceApi(IServiceProvider provider)
    {
        var findDto = provider.GetRequiredService<FindWorkspaceApiInformationFileDto>();
        var deleteFromApim = provider.GetRequiredService<DeleteWorkspaceApiFromApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        var taskDictionary = new ConcurrentDictionary<(WorkspaceApiName, WorkspaceName), AsyncLazy<Unit>>();

        return deleteApi;

        async ValueTask deleteApi(WorkspaceApiName name, WorkspaceName workspaceName, CancellationToken cancellationToken)
        {
            using var _ = activitySource.StartActivity(nameof(DeleteWorkspaceApi));

            await taskDictionary.GetOrAdd((name, workspaceName),
                                          x => new AsyncLazy<Unit>(async cancellationToken =>
                                          {
                                              var (name, workspaceName) = x;

                                              await WorkspaceApiName.TryParseRevisionedName(name)
                                                                    .Map(async api => await processRevisionedApi(name, api.RevisionNumber, workspaceName, cancellationToken))
                                                                    .IfLeft(async _ => await processRootApi(name, workspaceName, cancellationToken));

                                              return Unit.Default;
                                          }))
                                .WithCancellation(cancellationToken);

        }

        async ValueTask processRootApi(WorkspaceApiName name, WorkspaceName workspaceName, CancellationToken cancellationToken) =>
            await deleteFromApim(name, workspaceName, cancellationToken);

        async ValueTask processRevisionedApi(WorkspaceApiName name, ApiRevisionNumber revisionNumber, WorkspaceName workspaceName, CancellationToken cancellationToken)
        {
            var rootName = WorkspaceApiName.GetRootName(name);
            var currentRevisionNumberOption = await tryGetRevisionNumberInSourceControl(rootName, workspaceName, cancellationToken);

            await currentRevisionNumberOption.Match(// Only delete this revision if its number differs from the current source control revision number.
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
                                                            await deleteFromApim(name, workspaceName, cancellationToken);
                                                        }
                                                    },
                                                    // If there is no current revision in source control, process the root API deletion
                                                    async () => await deleteApi(rootName, workspaceName, cancellationToken));
        }

        async ValueTask<Option<ApiRevisionNumber>> tryGetRevisionNumberInSourceControl(WorkspaceApiName name, WorkspaceName workspaceName, CancellationToken cancellationToken)
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
            logger.LogInformation("Deleting API {WorkspaceApiName} in workspace {WorkspaceName}...", name, workspaceName);

            var resourceUri = WorkspaceApiUri.From(name, workspaceName, serviceUri);
            await (WorkspaceApiName.IsRevisioned(name)
                    ? resourceUri.Delete(pipeline, cancellationToken)
                    : resourceUri.DeleteAllRevisions(pipeline, cancellationToken));
        };
    }
}

file static class Common
{
    public static FrozenSet<string> SpecificationFileNames { get; } =
        new[]
        {
            WorkspaceWadlSpecificationFile.Name,
            WorkspaceWsdlSpecificationFile.Name,
            WorkspaceGraphQlSpecificationFile.Name,
            WorkspaceJsonOpenApiSpecificationFile.Name,
            WorkspaceYamlOpenApiSpecificationFile.Name
        }.ToFrozenSet();

    public static ApiRevisionNumber GetRevisionNumber(WorkspaceApiDto dto) =>
        ApiRevisionNumber.TryFrom(dto.Properties.ApiRevision)
                         .IfNone(() => ApiRevisionNumber.From(1));
}