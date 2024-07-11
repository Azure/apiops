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

public delegate ValueTask DeleteAllServicePolicies(ManagementServiceName serviceName, CancellationToken cancellationToken);
public delegate ValueTask PutServicePolicyModels(IEnumerable<ServicePolicyModel> models, ManagementServiceName serviceName, CancellationToken cancellationToken);
public delegate ValueTask ValidateExtractedServicePolicies(ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);
public delegate ValueTask<FrozenDictionary<ServicePolicyName, ServicePolicyDto>> GetApimServicePolicies(ManagementServiceName serviceName, CancellationToken cancellationToken);
public delegate ValueTask<FrozenDictionary<ServicePolicyName, ServicePolicyDto>> GetFileServicePolicies(ManagementServiceDirectory serviceDirectory, Option<CommitId> commitIdOption, CancellationToken cancellationToken);
public delegate ValueTask WriteServicePolicyModels(IEnumerable<ServicePolicyModel> models, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);
public delegate ValueTask ValidatePublishedServicePolicies(IDictionary<ServicePolicyName, ServicePolicyDto> overrides, Option<CommitId> commitIdOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

public static class ServicePolicyModule
{
    public static void ConfigureDeleteAllServicePolicies(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureGetManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteAllServicePolicies);
    }

    private static DeleteAllServicePolicies GetDeleteAllServicePolicies(IServiceProvider provider)
    {
        var getServiceUri = provider.GetRequiredService<GetManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (serviceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteAllServicePolicies));

            logger.LogInformation("Deleting all service policies in {ServiceName}...", serviceName);

            var serviceUri = getServiceUri(serviceName);

            await ServicePoliciesUri.From(serviceUri)
                                    .DeleteAll(pipeline, cancellationToken);
        };
    }

    public static void ConfigurePutServicePolicyModels(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureGetManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutServicePolicyModels);
    }

    private static PutServicePolicyModels GetPutServicePolicyModels(IServiceProvider provider)
    {
        var getServiceUri = provider.GetRequiredService<GetManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (models, serviceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutServicePolicyModels));

            logger.LogInformation("Putting policy fragment models in {ServiceName}...", serviceName);

            await models.IterParallel(async model =>
            {
                await put(model, serviceName, cancellationToken);
            }, cancellationToken);
        };

        async ValueTask put(ServicePolicyModel model, ManagementServiceName serviceName, CancellationToken cancellationToken)
        {
            var serviceUri = getServiceUri(serviceName);

            var dto = getDto(model);

            await ServicePolicyUri.From(model.Name, serviceUri)
                                  .PutDto(dto, pipeline, cancellationToken);
        }

        static ServicePolicyDto getDto(ServicePolicyModel model) =>
            new()
            {
                Properties = new ServicePolicyDto.ServicePolicyContract
                {
                    Format = "rawxml",
                    Value = model.Content
                }
            };
    }

    public static void ConfigureValidateExtractedServicePolicies(IHostApplicationBuilder builder)
    {
        ConfigureGetApimServicePolicies(builder);
        ConfigureGetFileServicePolicies(builder);

        builder.Services.TryAddSingleton(GetValidateExtractedServicePolicies);
    }

    private static ValidateExtractedServicePolicies GetValidateExtractedServicePolicies(IServiceProvider provider)
    {
        var getApimResources = provider.GetRequiredService<GetApimServicePolicies>();
        var tryGetApimGraphQlSchema = provider.GetRequiredService<TryGetApimGraphQlSchema>();
        var getFileResources = provider.GetRequiredService<GetFileServicePolicies>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (serviceName, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ValidateExtractedServicePolicies));

            logger.LogInformation("Validating extracted service policies in {ServiceName}...", serviceName);

            var apimResources = await getApimResources(serviceName, cancellationToken);
            var fileResources = await getFileResources(serviceDirectory, Prelude.None, cancellationToken);

            var expected = apimResources.MapValue(normalizeDto)
                                        .ToFrozenDictionary();

            var actual = fileResources.MapValue(normalizeDto)
                                      .ToFrozenDictionary();

            actual.Should().BeEquivalentTo(expected);
        };

        static string normalizeDto(ServicePolicyDto dto) =>
            new
            {
                Value = new string((dto.Properties.Value ?? string.Empty)
                                    .ReplaceLineEndings(string.Empty)
                                    .Where(c => char.IsWhiteSpace(c) is false)
                                    .ToArray())
            }.ToString()!;
    }

    public static void ConfigureGetApimServicePolicies(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureGetManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetGetApimServicePolicies);
    }

    private static GetApimServicePolicies GetGetApimServicePolicies(IServiceProvider provider)
    {
        var getServiceUri = provider.GetRequiredService<GetManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (serviceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(GetApimServicePolicies));

            logger.LogInformation("Getting service policies from {ServiceName}...", serviceName);

            var serviceUri = getServiceUri(serviceName);

            return await ServicePoliciesUri.From(serviceUri)
                                           .List(pipeline, cancellationToken)
                                           .ToFrozenDictionary(cancellationToken);
        };
    }

    public static void ConfigureGetFileServicePolicies(IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(GetGetFileServicePolicies);
    }

    private static GetFileServicePolicies GetGetFileServicePolicies(IServiceProvider provider)
    {
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (serviceDirectory, commitIdOption, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(GetFileServicePolicies));

            return await commitIdOption.Map(commitId => getWithCommit(serviceDirectory, commitId, cancellationToken))
                                       .IfNone(() => getWithoutCommit(serviceDirectory, cancellationToken));
        };

        async ValueTask<FrozenDictionary<ServicePolicyName, ServicePolicyDto>> getWithCommit(ManagementServiceDirectory serviceDirectory, CommitId commitId, CancellationToken cancellationToken)
        {
            using var _ = activitySource.StartActivity(nameof(GetFileServicePolicies));

            logger.LogInformation("Getting service policies from {ServiceDirectory} as of commit {CommitId}...", serviceDirectory, commitId);

            return await Git.GetExistingFilesInCommit(serviceDirectory.ToDirectoryInfo(), commitId)
                            .ToAsyncEnumerable()
                            .Choose(file => ServicePolicyFile.TryParse(file, serviceDirectory))
                            .Choose(async file => await tryGetCommitResource(commitId, serviceDirectory, file, cancellationToken))
                            .ToFrozenDictionary(cancellationToken);
        }

        static async ValueTask<Option<(ServicePolicyName name, ServicePolicyDto dto)>> tryGetCommitResource(CommitId commitId, ManagementServiceDirectory serviceDirectory, ServicePolicyFile file, CancellationToken cancellationToken)
        {
            var name = file.Name;
            var contentsOption = Git.TryGetFileContentsInCommit(serviceDirectory.ToDirectoryInfo(), file.ToFileInfo(), commitId);

            return await contentsOption.MapTask(async contents =>
            {
                using (contents)
                {
                    var data = await BinaryData.FromStreamAsync(contents, cancellationToken);
                    var dto = new ServicePolicyDto
                    {
                        Properties = new ServicePolicyDto.ServicePolicyContract
                        {
                            Value = data.ToString()
                        }
                    };
                    return (name, dto);
                }
            });
        }

        async ValueTask<FrozenDictionary<ServicePolicyName, ServicePolicyDto>> getWithoutCommit(ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
        {
            logger.LogInformation("Getting service policies from {ServiceDirectory}...", serviceDirectory);

            return await common.ServicePolicyModule.ListPolicyFiles(serviceDirectory)
                                                   .ToAsyncEnumerable()
                                                   .SelectAwait(async file => (file.Name,
                                                                               new ServicePolicyDto
                                                                               {
                                                                                   Properties = new ServicePolicyDto.ServicePolicyContract
                                                                                   {
                                                                                       Value = await file.ReadPolicy(cancellationToken)
                                                                                   }
                                                                               }))
                                                   .ToFrozenDictionary(cancellationToken);
        }
    }

    public static void ConfigureWriteServicePolicyModels(IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(GetWriteServicePolicyModels);
    }

    private static WriteServicePolicyModels GetWriteServicePolicyModels(IServiceProvider provider)
    {
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (models, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(WriteServicePolicyModels));

            logger.LogInformation("Writing policy fragment models to {ServiceDirectory}...", serviceDirectory);

            await models.IterParallel(async model =>
            {
                await writePolicyFile(model, serviceDirectory, cancellationToken);
            }, cancellationToken);
        };

        static async ValueTask writePolicyFile(ServicePolicyModel model, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
        {
            var policyFile = ServicePolicyFile.From(model.Name, serviceDirectory);
            await policyFile.WritePolicy(model.Content, cancellationToken);
        }
    }

    public static void ConfigureValidatePublishedServicePolicies(IHostApplicationBuilder builder)
    {
        ConfigureGetFileServicePolicies(builder);
        ConfigureGetApimServicePolicies(builder);

        builder.Services.TryAddSingleton(GetValidatePublishedServicePolicies);
    }

    private static ValidatePublishedServicePolicies GetValidatePublishedServicePolicies(IServiceProvider provider)
    {
        var getFileResources = provider.GetRequiredService<GetFileServicePolicies>();
        var getApimResources = provider.GetRequiredService<GetApimServicePolicies>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (overrides, commitIdOption, serviceName, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ValidatePublishedServicePolicies));

            logger.LogInformation("Validating published service policies in {ServiceDirectory}...", serviceDirectory);

            var apimResources = await getApimResources(serviceName, cancellationToken);
            var fileResources = await getFileResources(serviceDirectory, commitIdOption, cancellationToken);

            var expected = PublisherOptions.Override(fileResources, overrides)
                                           .MapValue(normalizeDto)
                                           .ToFrozenDictionary();

            var actual = apimResources.MapValue(normalizeDto)
                                      .ToFrozenDictionary();

            actual.Should().BeEquivalentTo(expected);
        };

        static string normalizeDto(ServicePolicyDto dto) =>
            new
            {
                Value = new string((dto.Properties.Value ?? string.Empty)
                                    .ReplaceLineEndings(string.Empty)
                                    .Where(c => char.IsWhiteSpace(c) is false)
                                    .ToArray())
            }.ToString()!;
    }

    public static Gen<ServicePolicyModel> GenerateUpdate(ServicePolicyModel original) =>
        from content in ServicePolicyModel.GenerateContent()
        select original with
        {
            Content = content
        };

    public static Gen<ServicePolicyDto> GenerateOverride(ServicePolicyDto original) =>
        from content in ServicePolicyModel.GenerateContent()
        select new ServicePolicyDto
        {
            Properties = new ServicePolicyDto.ServicePolicyContract
            {
                Value = content
            }
        };

    public static FrozenDictionary<ServicePolicyName, ServicePolicyDto> GetDtoDictionary(IEnumerable<ServicePolicyModel> models) =>
        models.ToFrozenDictionary(model => model.Name, GetDto);

    private static ServicePolicyDto GetDto(ServicePolicyModel model) =>
        new()
        {
            Properties = new ServicePolicyDto.ServicePolicyContract
            {
                Format = "rawxml",
                Value = model.Content
            }
        };
}