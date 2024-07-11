using Azure.Core.Pipeline;
using common;
using common.tests;
using CsCheck;
using FluentAssertions;
using LanguageExt;
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

public delegate ValueTask DeleteAllSubscriptions(ManagementServiceName serviceName, CancellationToken cancellationToken);
public delegate ValueTask PutSubscriptionModels(IEnumerable<SubscriptionModel> models, ManagementServiceName serviceName, CancellationToken cancellationToken);
public delegate ValueTask ValidateExtractedSubscriptions(Option<FrozenSet<SubscriptionName>> subscriptionNamesOption, Option<FrozenSet<ProductName>> productNamesOption, Option<FrozenSet<ApiName>> apiNamesOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);
public delegate ValueTask<FrozenDictionary<SubscriptionName, SubscriptionDto>> GetApimSubscriptions(ManagementServiceName serviceName, CancellationToken cancellationToken);
public delegate ValueTask<FrozenDictionary<SubscriptionName, SubscriptionDto>> GetFileSubscriptions(ManagementServiceDirectory serviceDirectory, Option<CommitId> commitIdOption, CancellationToken cancellationToken);
public delegate ValueTask WriteSubscriptionModels(IEnumerable<SubscriptionModel> models, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);
public delegate ValueTask ValidatePublishedSubscriptions(IDictionary<SubscriptionName, SubscriptionDto> overrides, Option<CommitId> commitIdOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

public static class SubscriptionModule
{
    public static void ConfigureDeleteAllSubscriptions(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureGetManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteAllSubscriptions);
    }

    private static DeleteAllSubscriptions GetDeleteAllSubscriptions(IServiceProvider provider)
    {
        var getServiceUri = provider.GetRequiredService<GetManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (serviceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteAllSubscriptions));

            logger.LogInformation("Deleting all subscriptions in {ServiceName}...", serviceName);

            var serviceUri = getServiceUri(serviceName);

            await SubscriptionsUri.From(serviceUri)
                                  .DeleteAll(pipeline, cancellationToken);
        };
    }

    public static void ConfigurePutSubscriptionModels(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureGetManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutSubscriptionModels);
    }

    private static PutSubscriptionModels GetPutSubscriptionModels(IServiceProvider provider)
    {
        var getServiceUri = provider.GetRequiredService<GetManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (models, serviceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutSubscriptionModels));

            logger.LogInformation("Putting subscription models in {ServiceName}...", serviceName);

            await models.IterParallel(async model =>
            {
                await put(model, serviceName, cancellationToken);
            }, cancellationToken);
        };

        async ValueTask put(SubscriptionModel model, ManagementServiceName serviceName, CancellationToken cancellationToken)
        {
            var serviceUri = getServiceUri(serviceName);

            var dto = getDto(model);

            await SubscriptionUri.From(model.Name, serviceUri)
                            .PutDto(dto, pipeline, cancellationToken);
        }

        static SubscriptionDto getDto(SubscriptionModel model) =>
            new()
            {
                Properties = new SubscriptionDto.SubscriptionContract
                {
                    DisplayName = model.DisplayName,
                    Scope = model.Scope switch
                    {
                        SubscriptionScope.Product product => $"/products/{product.Name}",
                        SubscriptionScope.Api api => $"/apis/{api.Name}",
                        _ => throw new InvalidOperationException($"Scope {model.Scope} not supported.")
                    }
                }
            };
    }

    public static void ConfigureValidateExtractedSubscriptions(IHostApplicationBuilder builder)
    {
        ConfigureGetApimSubscriptions(builder);
        ConfigureGetFileSubscriptions(builder);

        builder.Services.TryAddSingleton(GetValidateExtractedSubscriptions);
    }

    private static ValidateExtractedSubscriptions GetValidateExtractedSubscriptions(IServiceProvider provider)
    {
        var getApimResources = provider.GetRequiredService<GetApimSubscriptions>();
        var tryGetApimGraphQlSchema = provider.GetRequiredService<TryGetApimGraphQlSchema>();
        var getFileResources = provider.GetRequiredService<GetFileSubscriptions>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (namesFilterOption, productNamesFilterOption, apiNamesFilterOption, serviceName, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ValidateExtractedSubscriptions));

            logger.LogInformation("Validating extracted subscriptions in {ServiceName}...", serviceName);

            var apimResources = await getApimResources(serviceName, cancellationToken);
            var fileResources = await getFileResources(serviceDirectory, Prelude.None, cancellationToken);

            var expected = apimResources.WhereKey(name => ExtractorOptions.ShouldExtract(name, namesFilterOption))
                                        .WhereKey(name => name.Value.Equals("master", StringComparison.OrdinalIgnoreCase) is false)
                                        .WhereValue(dto => common.SubscriptionModule.TryGetProductName(dto)
                                                                                    .Map(name => ExtractorOptions.ShouldExtract(name, productNamesFilterOption))
                                                                                    .IfNone(true))
                                        .WhereValue(dto => common.SubscriptionModule.TryGetApiName(dto)
                                                                                    .Map(name => ExtractorOptions.ShouldExtract(name, apiNamesFilterOption))
                                                                                    .IfNone(true))
                                        .MapValue(normalizeDto)
                                        .ToFrozenDictionary();

            var actual = fileResources.MapValue(normalizeDto)
                                      .ToFrozenDictionary();

            actual.Should().BeEquivalentTo(expected);
        };

        static string normalizeDto(SubscriptionDto dto) =>
            new
            {
                DisplayName = dto.Properties.DisplayName ?? string.Empty,
                Scope = string.Join('/', dto.Properties.Scope?.Split('/')?.TakeLast(2)?.ToArray() ?? [])
            }.ToString()!;
    }

    public static void ConfigureGetApimSubscriptions(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureGetManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetGetApimSubscriptions);
    }

    private static GetApimSubscriptions GetGetApimSubscriptions(IServiceProvider provider)
    {
        var getServiceUri = provider.GetRequiredService<GetManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (serviceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(GetApimSubscriptions));

            logger.LogInformation("Getting subscriptions from {ServiceName}...", serviceName);

            var serviceUri = getServiceUri(serviceName);

            return await SubscriptionsUri.From(serviceUri)
                                         .List(pipeline, cancellationToken)
                                         .ToFrozenDictionary(cancellationToken);
        };
    }

    public static void ConfigureGetFileSubscriptions(IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(GetGetFileSubscriptions);
    }

    private static GetFileSubscriptions GetGetFileSubscriptions(IServiceProvider provider)
    {
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (serviceDirectory, commitIdOption, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(GetFileSubscriptions));

            return await commitIdOption.Map(commitId => getWithCommit(serviceDirectory, commitId, cancellationToken))
                                       .IfNone(() => getWithoutCommit(serviceDirectory, cancellationToken));
        };

        async ValueTask<FrozenDictionary<SubscriptionName, SubscriptionDto>> getWithCommit(ManagementServiceDirectory serviceDirectory, CommitId commitId, CancellationToken cancellationToken)
        {
            using var _ = activitySource.StartActivity(nameof(GetFileSubscriptions));

            logger.LogInformation("Getting subscriptions from {ServiceDirectory} as of commit {CommitId}...", serviceDirectory, commitId);

            return await Git.GetExistingFilesInCommit(serviceDirectory.ToDirectoryInfo(), commitId)
                            .ToAsyncEnumerable()
                            .Choose(file => SubscriptionInformationFile.TryParse(file, serviceDirectory))
                            .Choose(async file => await tryGetCommitResource(commitId, serviceDirectory, file, cancellationToken))
                            .ToFrozenDictionary(cancellationToken);
        }

        static async ValueTask<Option<(SubscriptionName name, SubscriptionDto dto)>> tryGetCommitResource(CommitId commitId, ManagementServiceDirectory serviceDirectory, SubscriptionInformationFile file, CancellationToken cancellationToken)
        {
            var name = file.Parent.Name;
            var contentsOption = Git.TryGetFileContentsInCommit(serviceDirectory.ToDirectoryInfo(), file.ToFileInfo(), commitId);

            return await contentsOption.MapTask(async contents =>
            {
                using (contents)
                {
                    var data = await BinaryData.FromStreamAsync(contents, cancellationToken);
                    var dto = data.ToObjectFromJson<SubscriptionDto>();
                    return (name, dto);
                }
            });
        }

        async ValueTask<FrozenDictionary<SubscriptionName, SubscriptionDto>> getWithoutCommit(ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
        {
            logger.LogInformation("Getting subscriptions from {ServiceDirectory}...", serviceDirectory);

            return await common.SubscriptionModule.ListInformationFiles(serviceDirectory)
                                             .ToAsyncEnumerable()
                                             .SelectAwait(async file => (file.Parent.Name,
                                                                         await file.ReadDto(cancellationToken)))
                                             .ToFrozenDictionary(cancellationToken);
        }
    }

    public static void ConfigureWriteSubscriptionModels(IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(GetWriteSubscriptionModels);
    }

    private static WriteSubscriptionModels GetWriteSubscriptionModels(IServiceProvider provider)
    {
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (models, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(WriteSubscriptionModels));

            logger.LogInformation("Writing subscription models to {ServiceDirectory}...", serviceDirectory);

            await models.IterParallel(async model =>
            {
                await writeInformationFile(model, serviceDirectory, cancellationToken);
            }, cancellationToken);
        };

        static async ValueTask writeInformationFile(SubscriptionModel model, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
        {
            var informationFile = SubscriptionInformationFile.From(model.Name, serviceDirectory);
            var dto = getDto(model);

            await informationFile.WriteDto(dto, cancellationToken);
        }

        static SubscriptionDto getDto(SubscriptionModel model) =>
            new()
            {
                Properties = new SubscriptionDto.SubscriptionContract
                {
                    DisplayName = model.DisplayName,
                    Scope = model.Scope switch
                    {
                        SubscriptionScope.Product product => $"/products/{product.Name}",
                        SubscriptionScope.Api api => $"/apis/{api.Name}",
                        _ => throw new InvalidOperationException($"Scope {model.Scope} not supported.")
                    }
                }
            };
    }

    public static void ConfigureValidatePublishedSubscriptions(IHostApplicationBuilder builder)
    {
        ConfigureGetFileSubscriptions(builder);
        ConfigureGetApimSubscriptions(builder);

        builder.Services.TryAddSingleton(GetValidatePublishedSubscriptions);
    }

    private static ValidatePublishedSubscriptions GetValidatePublishedSubscriptions(IServiceProvider provider)
    {
        var getFileResources = provider.GetRequiredService<GetFileSubscriptions>();
        var getApimResources = provider.GetRequiredService<GetApimSubscriptions>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (overrides, commitIdOption, serviceName, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ValidatePublishedSubscriptions));

            logger.LogInformation("Validating published subscriptions in {ServiceDirectory}...", serviceDirectory);

            var apimResources = await getApimResources(serviceName, cancellationToken);
            var fileResources = await getFileResources(serviceDirectory, commitIdOption, cancellationToken);

            var expected = PublisherOptions.Override(fileResources, overrides)
                                           .MapValue(normalizeDto)
                                           .ToFrozenDictionary();

            var actual = apimResources.MapValue(normalizeDto)
                                      .WhereKey(name => name.Value.Equals("master", StringComparison.OrdinalIgnoreCase) is false)
                                      .ToFrozenDictionary();

            actual.Should().BeEquivalentTo(expected);
        };

        static string normalizeDto(SubscriptionDto dto) =>
            new
            {
                DisplayName = dto.Properties.DisplayName ?? string.Empty,
                Scope = string.Join('/', dto.Properties.Scope?.Split('/')?.TakeLast(2)?.ToArray() ?? [])
            }.ToString()!;
    }

    public static Gen<SubscriptionModel> GenerateUpdate(SubscriptionModel original) =>
        from displayName in SubscriptionModel.GenerateDisplayName()
        from allowTracing in Gen.Bool.OptionOf()
        select original with
        {
            DisplayName = displayName
        };

    public static Gen<SubscriptionDto> GenerateOverride(SubscriptionDto original) =>
        from displayName in SubscriptionModel.GenerateDisplayName()
        select new SubscriptionDto
        {
            Properties = new SubscriptionDto.SubscriptionContract
            {
                DisplayName = displayName
            }
        };

    public static FrozenDictionary<SubscriptionName, SubscriptionDto> GetDtoDictionary(IEnumerable<SubscriptionModel> models) =>
        models.ToFrozenDictionary(model => model.Name, GetDto);

    private static SubscriptionDto GetDto(SubscriptionModel model) =>
        new()
        {
            Properties = new SubscriptionDto.SubscriptionContract
            {
                DisplayName = model.DisplayName,
                Scope = model.Scope switch
                {
                    SubscriptionScope.Product product => $"/products/{product.Name}",
                    SubscriptionScope.Api api => $"/apis/{api.Name}",
                    _ => throw new InvalidOperationException($"Scope {model.Scope} not supported.")
                }
            }
        };
}