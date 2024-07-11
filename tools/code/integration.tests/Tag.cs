using Azure.Core.Pipeline;
using common;
using common.tests;
using CsCheck;
using FluentAssertions;
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

public delegate ValueTask DeleteAllTags(ManagementServiceName serviceName, CancellationToken cancellationToken);
public delegate ValueTask PutTagModels(IEnumerable<TagModel> models, ManagementServiceName serviceName, CancellationToken cancellationToken);
public delegate ValueTask ValidateExtractedTags(Option<FrozenSet<TagName>> tagNamesOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);
public delegate ValueTask<FrozenDictionary<TagName, TagDto>> GetApimTags(ManagementServiceName serviceName, CancellationToken cancellationToken);
public delegate ValueTask<FrozenDictionary<TagName, TagDto>> GetFileTags(ManagementServiceDirectory serviceDirectory, Option<CommitId> commitIdOption, CancellationToken cancellationToken);
public delegate ValueTask WriteTagModels(IEnumerable<TagModel> models, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);
public delegate ValueTask ValidatePublishedTags(IDictionary<TagName, TagDto> overrides, Option<CommitId> commitIdOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

public static class TagModule
{
    public static void ConfigureDeleteAllTags(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureGetManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteAllTags);
    }

    private static DeleteAllTags GetDeleteAllTags(IServiceProvider provider)
    {
        var getServiceUri = provider.GetRequiredService<GetManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (serviceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteAllTags));

            logger.LogInformation("Deleting all tags in {ServiceName}...", serviceName);

            var serviceUri = getServiceUri(serviceName);

            await TagsUri.From(serviceUri)
                         .DeleteAll(pipeline, cancellationToken);
        };
    }

    public static void ConfigurePutTagModels(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureGetManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutTagModels);
    }

    private static PutTagModels GetPutTagModels(IServiceProvider provider)
    {
        var getServiceUri = provider.GetRequiredService<GetManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (models, serviceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutTagModels));

            logger.LogInformation("Putting tag models in {ServiceName}...", serviceName);

            await models.IterParallel(async model =>
            {
                await put(model, serviceName, cancellationToken);
            }, cancellationToken);
        };

        async ValueTask put(TagModel model, ManagementServiceName serviceName, CancellationToken cancellationToken)
        {
            var serviceUri = getServiceUri(serviceName);

            var dto = getDto(model);

            await TagUri.From(model.Name, serviceUri)
                            .PutDto(dto, pipeline, cancellationToken);
        }

        static TagDto getDto(TagModel model) =>
            new()
            {
                Properties = new TagDto.TagContract
                {
                    DisplayName = model.DisplayName
                }
            };
    }

    public static void ConfigureValidateExtractedTags(IHostApplicationBuilder builder)
    {
        ConfigureGetApimTags(builder);
        ConfigureGetFileTags(builder);

        builder.Services.TryAddSingleton(GetValidateExtractedTags);
    }

    private static ValidateExtractedTags GetValidateExtractedTags(IServiceProvider provider)
    {
        var getApimResources = provider.GetRequiredService<GetApimTags>();
        var tryGetApimGraphQlSchema = provider.GetRequiredService<TryGetApimGraphQlSchema>();
        var getFileResources = provider.GetRequiredService<GetFileTags>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (namesFilterOption, serviceName, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ValidateExtractedTags));

            logger.LogInformation("Validating extracted tags in {ServiceName}...", serviceName);

            var apimResources = await getApimResources(serviceName, cancellationToken);
            var fileResources = await getFileResources(serviceDirectory, Prelude.None, cancellationToken);

            var expected = apimResources.WhereKey(name => ExtractorOptions.ShouldExtract(name, namesFilterOption))
                                        .MapValue(normalizeDto)
                                        .ToFrozenDictionary();

            var actual = fileResources.MapValue(normalizeDto)
                                      .ToFrozenDictionary();

            actual.Should().BeEquivalentTo(expected);
        };

        static string normalizeDto(TagDto dto) =>
            new
            {
                DisplayName = dto.Properties.DisplayName ?? string.Empty
            }.ToString()!;
    }

    public static void ConfigureGetApimTags(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureGetManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetGetApimTags);
    }

    private static GetApimTags GetGetApimTags(IServiceProvider provider)
    {
        var getServiceUri = provider.GetRequiredService<GetManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (serviceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(GetApimTags));

            logger.LogInformation("Getting tags from {ServiceName}...", serviceName);

            var serviceUri = getServiceUri(serviceName);

            return await TagsUri.From(serviceUri)
                                .List(pipeline, cancellationToken)
                                .ToFrozenDictionary(cancellationToken);
        };
    }

    public static void ConfigureGetFileTags(IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(GetGetFileTags);
    }

    private static GetFileTags GetGetFileTags(IServiceProvider provider)
    {
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (serviceDirectory, commitIdOption, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(GetFileTags));

            return await commitIdOption.Map(commitId => getWithCommit(serviceDirectory, commitId, cancellationToken))
                                       .IfNone(() => getWithoutCommit(serviceDirectory, cancellationToken));
        };

        async ValueTask<FrozenDictionary<TagName, TagDto>> getWithCommit(ManagementServiceDirectory serviceDirectory, CommitId commitId, CancellationToken cancellationToken)
        {
            using var _ = activitySource.StartActivity(nameof(GetFileTags));

            logger.LogInformation("Getting tags from {ServiceDirectory} as of commit {CommitId}...", serviceDirectory, commitId);

            return await Git.GetExistingFilesInCommit(serviceDirectory.ToDirectoryInfo(), commitId)
                            .ToAsyncEnumerable()
                            .Choose(file => TagInformationFile.TryParse(file, serviceDirectory))
                            .Choose(async file => await tryGetCommitResource(commitId, serviceDirectory, file, cancellationToken))
                            .ToFrozenDictionary(cancellationToken);
        }

        static async ValueTask<Option<(TagName name, TagDto dto)>> tryGetCommitResource(CommitId commitId, ManagementServiceDirectory serviceDirectory, TagInformationFile file, CancellationToken cancellationToken)
        {
            var name = file.Parent.Name;
            var contentsOption = Git.TryGetFileContentsInCommit(serviceDirectory.ToDirectoryInfo(), file.ToFileInfo(), commitId);

            return await contentsOption.MapTask(async contents =>
            {
                using (contents)
                {
                    var data = await BinaryData.FromStreamAsync(contents, cancellationToken);
                    var dto = data.ToObjectFromJson<TagDto>();
                    return (name, dto);
                }
            });
        }

        async ValueTask<FrozenDictionary<TagName, TagDto>> getWithoutCommit(ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
        {
            logger.LogInformation("Getting tags from {ServiceDirectory}...", serviceDirectory);

            return await common.TagModule.ListInformationFiles(serviceDirectory)
                                         .ToAsyncEnumerable()
                                         .SelectAwait(async file => (file.Parent.Name,
                                                                     await file.ReadDto(cancellationToken)))
                                         .ToFrozenDictionary(cancellationToken);
        }
    }

    public static void ConfigureWriteTagModels(IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(GetWriteTagModels);
    }

    private static WriteTagModels GetWriteTagModels(IServiceProvider provider)
    {
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (models, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(WriteTagModels));

            logger.LogInformation("Writing tag models to {ServiceDirectory}...", serviceDirectory);

            await models.IterParallel(async model =>
            {
                await writeInformationFile(model, serviceDirectory, cancellationToken);
            }, cancellationToken);
        };

        static async ValueTask writeInformationFile(TagModel model, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
        {
            var informationFile = TagInformationFile.From(model.Name, serviceDirectory);
            var dto = getDto(model);

            await informationFile.WriteDto(dto, cancellationToken);
        }

        static TagDto getDto(TagModel model) =>
            new()
            {
                Properties = new TagDto.TagContract
                {
                    DisplayName = model.DisplayName
                }
            };
    }

    public static void ConfigureValidatePublishedTags(IHostApplicationBuilder builder)
    {
        ConfigureGetFileTags(builder);
        ConfigureGetApimTags(builder);

        builder.Services.TryAddSingleton(GetValidatePublishedTags);
    }

    private static ValidatePublishedTags GetValidatePublishedTags(IServiceProvider provider)
    {
        var getFileResources = provider.GetRequiredService<GetFileTags>();
        var getApimResources = provider.GetRequiredService<GetApimTags>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (overrides, commitIdOption, serviceName, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ValidatePublishedTags));

            logger.LogInformation("Validating published tags in {ServiceDirectory}...", serviceDirectory);

            var apimResources = await getApimResources(serviceName, cancellationToken);
            var fileResources = await getFileResources(serviceDirectory, commitIdOption, cancellationToken);

            var expected = PublisherOptions.Override(fileResources, overrides)
                                           .MapValue(normalizeDto)
                                           .ToFrozenDictionary();

            var actual = apimResources.MapValue(normalizeDto)
                                      .ToFrozenDictionary();

            actual.Should().BeEquivalentTo(expected);
        };

        static string normalizeDto(TagDto dto) =>
        new
        {
            DisplayName = dto.Properties.DisplayName ?? string.Empty
        }.ToString()!;
    }
    public static Gen<TagModel> GenerateUpdate(TagModel original) =>
        from displayName in TagModel.GenerateDisplayName()
        select original with
        {
            DisplayName = displayName
        };

    public static Gen<TagDto> GenerateOverride(TagDto original) =>
        from displayName in TagModel.GenerateDisplayName()
        select new TagDto
        {
            Properties = new TagDto.TagContract
            {
                DisplayName = displayName
            }
        };

    public static FrozenDictionary<TagName, TagDto> GetDtoDictionary(IEnumerable<TagModel> models) =>
        models.ToFrozenDictionary(model => model.Name, GetDto);

    private static TagDto GetDto(TagModel model) =>
        new()
        {
            Properties = new TagDto.TagContract
            {
                DisplayName = model.DisplayName
            }
        };
}