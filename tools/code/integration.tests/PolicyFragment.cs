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

public delegate ValueTask DeleteAllPolicyFragments(ManagementServiceName serviceName, CancellationToken cancellationToken);
public delegate ValueTask PutPolicyFragmentModels(IEnumerable<PolicyFragmentModel> models, ManagementServiceName serviceName, CancellationToken cancellationToken);
public delegate ValueTask ValidateExtractedPolicyFragments(Option<FrozenSet<PolicyFragmentName>> policyfragmentNamesOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);
public delegate ValueTask<FrozenDictionary<PolicyFragmentName, PolicyFragmentDto>> GetApimPolicyFragments(ManagementServiceName serviceName, CancellationToken cancellationToken);
public delegate ValueTask<FrozenDictionary<PolicyFragmentName, PolicyFragmentDto>> GetFilePolicyFragments(ManagementServiceDirectory serviceDirectory, Option<CommitId> commitIdOption, CancellationToken cancellationToken);
public delegate ValueTask WritePolicyFragmentModels(IEnumerable<PolicyFragmentModel> models, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);
public delegate ValueTask ValidatePublishedPolicyFragments(IDictionary<PolicyFragmentName, PolicyFragmentDto> overrides, Option<CommitId> commitIdOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

public static class PolicyFragmentModule
{
    public static void ConfigureDeleteAllPolicyFragments(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureGetManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteAllPolicyFragments);
    }

    private static DeleteAllPolicyFragments GetDeleteAllPolicyFragments(IServiceProvider provider)
    {
        var getServiceUri = provider.GetRequiredService<GetManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (serviceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteAllPolicyFragments));

            logger.LogInformation("Deleting all policy fragments in {ServiceName}...", serviceName);

            var serviceUri = getServiceUri(serviceName);

            await PolicyFragmentsUri.From(serviceUri)
                                    .DeleteAll(pipeline, cancellationToken);
        };
    }

    public static void ConfigurePutPolicyFragmentModels(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureGetManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutPolicyFragmentModels);
    }

    private static PutPolicyFragmentModels GetPutPolicyFragmentModels(IServiceProvider provider)
    {
        var getServiceUri = provider.GetRequiredService<GetManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (models, serviceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutPolicyFragmentModels));

            logger.LogInformation("Putting policy fragment models in {ServiceName}...", serviceName);

            await models.IterParallel(async model =>
            {
                await put(model, serviceName, cancellationToken);
            }, cancellationToken);
        };

        async ValueTask put(PolicyFragmentModel model, ManagementServiceName serviceName, CancellationToken cancellationToken)
        {
            var serviceUri = getServiceUri(serviceName);

            var dto = getDto(model);

            await PolicyFragmentUri.From(model.Name, serviceUri)
                               .PutDto(dto, pipeline, cancellationToken);
        }

        static PolicyFragmentDto getDto(PolicyFragmentModel model) =>
            new()
            {
                Properties = new PolicyFragmentDto.PolicyFragmentContract
                {
                    Description = model.Description.ValueUnsafe(),
                    Format = "rawxml",
                    Value = model.Content
                }
            };
    }

    public static void ConfigureValidateExtractedPolicyFragments(IHostApplicationBuilder builder)
    {
        ConfigureGetApimPolicyFragments(builder);
        ConfigureGetFilePolicyFragments(builder);

        builder.Services.TryAddSingleton(GetValidateExtractedPolicyFragments);
    }

    private static ValidateExtractedPolicyFragments GetValidateExtractedPolicyFragments(IServiceProvider provider)
    {
        var getApimResources = provider.GetRequiredService<GetApimPolicyFragments>();
        var tryGetApimGraphQlSchema = provider.GetRequiredService<TryGetApimGraphQlSchema>();
        var getFileResources = provider.GetRequiredService<GetFilePolicyFragments>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (namesFilterOption, serviceName, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ValidateExtractedPolicyFragments));

            logger.LogInformation("Validating extracted policy fragments in {ServiceName}...", serviceName);

            var apimResources = await getApimResources(serviceName, cancellationToken);
            var fileResources = await getFileResources(serviceDirectory, Prelude.None, cancellationToken);

            var expected = apimResources.WhereKey(name => ExtractorOptions.ShouldExtract(name, namesFilterOption))
                                        .MapValue(normalizeDto)
                                        .ToFrozenDictionary();

            var actual = fileResources.MapValue(normalizeDto)
                                      .ToFrozenDictionary();

            actual.Should().BeEquivalentTo(expected);
        };

        static string normalizeDto(PolicyFragmentDto dto) =>
            new
            {
                Description = dto.Properties.Description ?? string.Empty
            }.ToString()!;
    }

    public static void ConfigureGetApimPolicyFragments(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureGetManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetGetApimPolicyFragments);
    }

    private static GetApimPolicyFragments GetGetApimPolicyFragments(IServiceProvider provider)
    {
        var getServiceUri = provider.GetRequiredService<GetManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (serviceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(GetApimPolicyFragments));

            logger.LogInformation("Getting policy fragments from {ServiceName}...", serviceName);

            var serviceUri = getServiceUri(serviceName);

            return await PolicyFragmentsUri.From(serviceUri)
                                           .List(pipeline, cancellationToken)
                                           .ToFrozenDictionary(cancellationToken);
        };
    }

    public static void ConfigureGetFilePolicyFragments(IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(GetGetFilePolicyFragments);
    }

    private static GetFilePolicyFragments GetGetFilePolicyFragments(IServiceProvider provider)
    {
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (serviceDirectory, commitIdOption, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(GetFilePolicyFragments));

            return await commitIdOption.Map(commitId => getWithCommit(serviceDirectory, commitId, cancellationToken))
                                       .IfNone(() => getWithoutCommit(serviceDirectory, cancellationToken));
        };

        async ValueTask<FrozenDictionary<PolicyFragmentName, PolicyFragmentDto>> getWithCommit(ManagementServiceDirectory serviceDirectory, CommitId commitId, CancellationToken cancellationToken)
        {
            using var _ = activitySource.StartActivity(nameof(GetFilePolicyFragments));

            logger.LogInformation("Getting policy fragments from {ServiceDirectory} as of commit {CommitId}...", serviceDirectory, commitId);

            return await Git.GetExistingFilesInCommit(serviceDirectory.ToDirectoryInfo(), commitId)
                            .ToAsyncEnumerable()
                            .Choose(file => PolicyFragmentInformationFile.TryParse(file, serviceDirectory))
                            .Choose(async file => await tryGetCommitResource(commitId, serviceDirectory, file, cancellationToken))
                            .ToFrozenDictionary(cancellationToken);
        }

        static async ValueTask<Option<(PolicyFragmentName name, PolicyFragmentDto dto)>> tryGetCommitResource(CommitId commitId, ManagementServiceDirectory serviceDirectory, PolicyFragmentInformationFile file, CancellationToken cancellationToken)
        {
            var name = file.Parent.Name;
            var contentsOption = Git.TryGetFileContentsInCommit(serviceDirectory.ToDirectoryInfo(), file.ToFileInfo(), commitId);

            return await contentsOption.MapTask(async contents =>
            {
                using (contents)
                {
                    var data = await BinaryData.FromStreamAsync(contents, cancellationToken);
                    var dto = data.ToObjectFromJson<PolicyFragmentDto>();
                    return (name, dto);
                }
            });
        }

        async ValueTask<FrozenDictionary<PolicyFragmentName, PolicyFragmentDto>> getWithoutCommit(ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
        {
            logger.LogInformation("Getting policy fragments from {ServiceDirectory}...", serviceDirectory);

            return await common.PolicyFragmentModule.ListInformationFiles(serviceDirectory)
                                                    .ToAsyncEnumerable()
                                                    .SelectAwait(async file => (file.Parent.Name,
                                                                                await file.ReadDto(cancellationToken)))
                                                    .ToFrozenDictionary(cancellationToken);
        }
    }

    public static void ConfigureWritePolicyFragmentModels(IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(GetWritePolicyFragmentModels);
    }

    private static WritePolicyFragmentModels GetWritePolicyFragmentModels(IServiceProvider provider)
    {
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (models, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(WritePolicyFragmentModels));

            logger.LogInformation("Writing policy fragment models to {ServiceDirectory}...", serviceDirectory);

            await models.IterParallel(async model =>
            {
                await writeInformationFile(model, serviceDirectory, cancellationToken);
                await writePolicyFile(model, serviceDirectory, cancellationToken);
            }, cancellationToken);
        };

        static async ValueTask writeInformationFile(PolicyFragmentModel model, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
        {
            var informationFile = PolicyFragmentInformationFile.From(model.Name, serviceDirectory);
            var dto = getDto(model);

            await informationFile.WriteDto(dto, cancellationToken);
        }

        static PolicyFragmentDto getDto(PolicyFragmentModel model) =>
            new()
            {
                Properties = new PolicyFragmentDto.PolicyFragmentContract
                {
                    Description = model.Description.ValueUnsafe(),
                    Format = "rawxml",
                    Value = model.Content
                }
            };

        static async ValueTask writePolicyFile(PolicyFragmentModel model, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
        {
            var policyFile = PolicyFragmentPolicyFile.From(model.Name, serviceDirectory);
            await policyFile.WritePolicy(model.Content, cancellationToken);
        }
    }

    public static void ConfigureValidatePublishedPolicyFragments(IHostApplicationBuilder builder)
    {
        ConfigureGetFilePolicyFragments(builder);
        ConfigureGetApimPolicyFragments(builder);

        builder.Services.TryAddSingleton(GetValidatePublishedPolicyFragments);
    }

    private static ValidatePublishedPolicyFragments GetValidatePublishedPolicyFragments(IServiceProvider provider)
    {
        var getFileResources = provider.GetRequiredService<GetFilePolicyFragments>();
        var getApimResources = provider.GetRequiredService<GetApimPolicyFragments>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (overrides, commitIdOption, serviceName, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ValidatePublishedPolicyFragments));

            logger.LogInformation("Validating published policy fragments in {ServiceDirectory}...", serviceDirectory);

            var apimResources = await getApimResources(serviceName, cancellationToken);
            var fileResources = await getFileResources(serviceDirectory, commitIdOption, cancellationToken);

            var expected = PublisherOptions.Override(fileResources, overrides)
                                           .MapValue(normalizeDto)
                                           .ToFrozenDictionary();

            var actual = apimResources.MapValue(normalizeDto)
                                      .ToFrozenDictionary();

            actual.Should().BeEquivalentTo(expected);
        };

        static string normalizeDto(PolicyFragmentDto dto) =>
            new
            {
                Description = dto.Properties.Description ?? string.Empty
            }.ToString()!;
    }

    public static Gen<PolicyFragmentModel> GenerateUpdate(PolicyFragmentModel original) =>
        from description in PolicyFragmentModel.GenerateDescription().OptionOf()
        from content in PolicyFragmentModel.GenerateContent()
        select original with
        {
            Description = description,
            Content = content
        };

    public static Gen<PolicyFragmentDto> GenerateOverride(PolicyFragmentDto original) =>
        from description in PolicyFragmentModel.GenerateDescription().OptionOf()
        from content in PolicyFragmentModel.GenerateContent()
        select new PolicyFragmentDto
        {
            Properties = new PolicyFragmentDto.PolicyFragmentContract
            {
                Description = description.ValueUnsafe(),
                Format = "rawxml",
                Value = content
            }
        };

    public static FrozenDictionary<PolicyFragmentName, PolicyFragmentDto> GetDtoDictionary(IEnumerable<PolicyFragmentModel> models) =>
        models.ToFrozenDictionary(model => model.Name, GetDto);

    private static PolicyFragmentDto GetDto(PolicyFragmentModel model) =>
        new()
        {
            Properties = new PolicyFragmentDto.PolicyFragmentContract
            {
                Description = model.Description.ValueUnsafe(),
                Format = "rawxml",
                Value = model.Content
            }
        };
}