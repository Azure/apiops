using Azure.Core.Pipeline;
using common;
using common.tests;
using CsCheck;
using FluentAssertions;
using LanguageExt;
using LanguageExt.UnsafeValueAccess;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

internal delegate ValueTask DeleteAllPolicyFragments(ManagementServiceName serviceName, CancellationToken cancellationToken);

internal delegate ValueTask PutPolicyFragmentModels(IEnumerable<PolicyFragmentModel> models, ManagementServiceName serviceName, CancellationToken cancellationToken);

internal delegate ValueTask ValidateExtractedPolicyFragments(Option<FrozenSet<PolicyFragmentName>> namesFilterOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

file delegate ValueTask<FrozenDictionary<PolicyFragmentName, PolicyFragmentDto>> GetApimPolicyFragments(ManagementServiceName serviceName, CancellationToken cancellationToken);

file delegate ValueTask<FrozenDictionary<PolicyFragmentName, PolicyFragmentDto>> GetFilePolicyFragments(ManagementServiceDirectory serviceDirectory, Option<CommitId> commitIdOption, CancellationToken cancellationToken);

internal delegate ValueTask WritePolicyFragmentModels(IEnumerable<PolicyFragmentModel> models, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

internal delegate ValueTask ValidatePublishedPolicyFragments(IDictionary<PolicyFragmentName, PolicyFragmentDto> overrides, Option<CommitId> commitIdOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

file sealed class DeleteAllPolicyFragmentsHandler(ILogger<DeleteAllPolicyFragments> logger, GetManagementServiceUri getServiceUri, HttpPipeline pipeline, ActivitySource activitySource)
{
    public async ValueTask Handle(ManagementServiceName serviceName, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(DeleteAllPolicyFragments));

        logger.LogInformation("Deleting all policy fragments in {ServiceName}...", serviceName);
        var serviceUri = getServiceUri(serviceName);
        await PolicyFragmentsUri.From(serviceUri).DeleteAll(pipeline, cancellationToken);
    }
}

file sealed class PutPolicyFragmentModelsHandler(ILogger<PutPolicyFragmentModels> logger, GetManagementServiceUri getServiceUri, HttpPipeline pipeline, ActivitySource activitySource)
{
    public async ValueTask Handle(IEnumerable<PolicyFragmentModel> models, ManagementServiceName serviceName, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(PutPolicyFragmentModels));

        logger.LogInformation("Putting policy fragment models in {ServiceName}...", serviceName);
        await models.IterParallel(async model =>
        {
            await Put(model, serviceName, cancellationToken);
        }, cancellationToken);
    }

    private async ValueTask Put(PolicyFragmentModel model, ManagementServiceName serviceName, CancellationToken cancellationToken)
    {
        var serviceUri = getServiceUri(serviceName);
        var uri = PolicyFragmentUri.From(model.Name, serviceUri);
        var dto = GetDto(model);

        await uri.PutDto(dto, pipeline, cancellationToken);
    }

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

file sealed class ValidateExtractedPolicyFragmentsHandler(ILogger<ValidateExtractedPolicyFragments> logger, GetApimPolicyFragments getApimResources, GetFilePolicyFragments getFileResources, ActivitySource activitySource)
{
    public async ValueTask Handle(Option<FrozenSet<PolicyFragmentName>> namesFilterOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(ValidateExtractedPolicyFragments));

        logger.LogInformation("Validating extracted policy fragments in {ServiceName}...", serviceName);
        var apimResources = await getApimResources(serviceName, cancellationToken);
        var fileResources = await getFileResources(serviceDirectory, Prelude.None, cancellationToken);

        var expected = apimResources.WhereKey(name => ExtractorOptions.ShouldExtract(name, namesFilterOption))
                                    .MapValue(NormalizeDto);
        var actual = fileResources.MapValue(NormalizeDto);

        actual.Should().BeEquivalentTo(expected);
    }

    private static string NormalizeDto(PolicyFragmentDto dto) =>
        new
        {
            Description = dto.Properties.Description ?? string.Empty
        }.ToString()!;
}

file sealed class GetApimPolicyFragmentsHandler(ILogger<GetApimPolicyFragments> logger, GetManagementServiceUri getServiceUri, HttpPipeline pipeline, ActivitySource activitySource)
{
    public async ValueTask<FrozenDictionary<PolicyFragmentName, PolicyFragmentDto>> Handle(ManagementServiceName serviceName, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(GetApimPolicyFragments));

        logger.LogInformation("Getting policy fragments from {ServiceName}...", serviceName);

        var serviceUri = getServiceUri(serviceName);
        var uri = PolicyFragmentsUri.From(serviceUri);

        return await uri.List(pipeline, cancellationToken)
                        .ToFrozenDictionary(cancellationToken);
    }
}

file sealed class GetFilePolicyFragmentsHandler(ILogger<GetFilePolicyFragments> logger, ActivitySource activitySource)
{
    public async ValueTask<FrozenDictionary<PolicyFragmentName, PolicyFragmentDto>> Handle(ManagementServiceDirectory serviceDirectory, Option<CommitId> commitIdOption, CancellationToken cancellationToken) =>
        await commitIdOption.Map(commitId => GetWithCommit(serviceDirectory, commitId, cancellationToken))
                           .IfNone(() => GetWithoutCommit(serviceDirectory, cancellationToken));

    private async ValueTask<FrozenDictionary<PolicyFragmentName, PolicyFragmentDto>> GetWithCommit(ManagementServiceDirectory serviceDirectory, CommitId commitId, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(GetFilePolicyFragments));

        logger.LogInformation("Getting policy fragments from {ServiceDirectory} as of commit {CommitId}...", serviceDirectory, commitId);

        return await Git.GetExistingFilesInCommit(serviceDirectory.ToDirectoryInfo(), commitId)
                        .ToAsyncEnumerable()
                        .Choose(file => PolicyFragmentInformationFile.TryParse(file, serviceDirectory))
                        .Choose(async file => await TryGetCommitResource(commitId, serviceDirectory, file, cancellationToken))
                        .ToFrozenDictionary(cancellationToken);
    }

    private static async ValueTask<Option<(PolicyFragmentName name, PolicyFragmentDto dto)>> TryGetCommitResource(CommitId commitId, ManagementServiceDirectory serviceDirectory, PolicyFragmentInformationFile file, CancellationToken cancellationToken)
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

    private async ValueTask<FrozenDictionary<PolicyFragmentName, PolicyFragmentDto>> GetWithoutCommit(ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(GetFilePolicyFragments));

        logger.LogInformation("Getting policy fragments from {ServiceDirectory}...", serviceDirectory);

        return await PolicyFragmentModule.ListInformationFiles(serviceDirectory)
                                     .ToAsyncEnumerable()
                                     .SelectAwait(async file => (file.Parent.Name,
                                                                 await file.ReadDto(cancellationToken)))
                                     .ToFrozenDictionary(cancellationToken);
    }
}

file sealed class WritePolicyFragmentModelsHandler(ILogger<WritePolicyFragmentModels> logger, ActivitySource activitySource)
{
    public async ValueTask Handle(IEnumerable<PolicyFragmentModel> models, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(WritePolicyFragmentModels));

        logger.LogInformation("Writing policy fragment models to {ServiceDirectory}...", serviceDirectory);
        await models.IterParallel(async model =>
        {
            await WriteInformationFile(model, serviceDirectory, cancellationToken);
            await WritePolicyFile(model, serviceDirectory, cancellationToken);
        }, cancellationToken);
    }

    private static async ValueTask WriteInformationFile(PolicyFragmentModel model, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        var informationFile = PolicyFragmentInformationFile.From(model.Name, serviceDirectory);
        var dto = GetDto(model);

        await informationFile.WriteDto(dto, cancellationToken);
    }

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

    private static async ValueTask WritePolicyFile(PolicyFragmentModel model, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        var policyFile = PolicyFragmentPolicyFile.From(model.Name, serviceDirectory);
        await policyFile.WritePolicy(model.Content, cancellationToken);
    }
}

file sealed class ValidatePublishedPolicyFragmentsHandler(ILogger<ValidatePublishedPolicyFragments> logger, GetFilePolicyFragments getFileResources, GetApimPolicyFragments getApimResources, ActivitySource activitySource)
{
    public async ValueTask Handle(IDictionary<PolicyFragmentName, PolicyFragmentDto> overrides, Option<CommitId> commitIdOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(ValidatePublishedPolicyFragments));

        logger.LogInformation("Validating published policy fragments in {ServiceDirectory}...", serviceDirectory);

        var apimResources = await getApimResources(serviceName, cancellationToken);
        var fileResources = await getFileResources(serviceDirectory, commitIdOption, cancellationToken);

        var expected = PublisherOptions.Override(fileResources, overrides)
                                       .MapValue(NormalizeDto);
        var actual = apimResources.MapValue(NormalizeDto);

        actual.Should().BeEquivalentTo(expected);
    }

    private static string NormalizeDto(PolicyFragmentDto dto) =>
        new
        {
            Description = dto.Properties.Description ?? string.Empty
        }.ToString()!;
}

internal static class PolicyFragmentServices
{
    public static void ConfigureDeleteAllPolicyFragments(IServiceCollection services)
    {
        ManagementServices.ConfigureGetManagementServiceUri(services);

        services.TryAddSingleton<DeleteAllPolicyFragmentsHandler>();
        services.TryAddSingleton<DeleteAllPolicyFragments>(provider => provider.GetRequiredService<DeleteAllPolicyFragmentsHandler>().Handle);
    }

    public static void ConfigurePutPolicyFragmentModels(IServiceCollection services)
    {
        ManagementServices.ConfigureGetManagementServiceUri(services);

        services.TryAddSingleton<PutPolicyFragmentModelsHandler>();
        services.TryAddSingleton<PutPolicyFragmentModels>(provider => provider.GetRequiredService<PutPolicyFragmentModelsHandler>().Handle);
    }

    public static void ConfigureValidateExtractedPolicyFragments(IServiceCollection services)
    {
        ConfigureGetApimPolicyFragments(services);
        ConfigureGetFilePolicyFragments(services);

        services.TryAddSingleton<ValidateExtractedPolicyFragmentsHandler>();
        services.TryAddSingleton<ValidateExtractedPolicyFragments>(provider => provider.GetRequiredService<ValidateExtractedPolicyFragmentsHandler>().Handle);
    }

    private static void ConfigureGetApimPolicyFragments(IServiceCollection services)
    {
        ManagementServices.ConfigureGetManagementServiceUri(services);

        services.TryAddSingleton<GetApimPolicyFragmentsHandler>();
        services.TryAddSingleton<GetApimPolicyFragments>(provider => provider.GetRequiredService<GetApimPolicyFragmentsHandler>().Handle);
    }

    private static void ConfigureGetFilePolicyFragments(IServiceCollection services)
    {
        services.TryAddSingleton<GetFilePolicyFragmentsHandler>();
        services.TryAddSingleton<GetFilePolicyFragments>(provider => provider.GetRequiredService<GetFilePolicyFragmentsHandler>().Handle);
    }

    public static void ConfigureWritePolicyFragmentModels(IServiceCollection services)
    {
        services.TryAddSingleton<WritePolicyFragmentModelsHandler>();
        services.TryAddSingleton<WritePolicyFragmentModels>(provider => provider.GetRequiredService<WritePolicyFragmentModelsHandler>().Handle);
    }

    public static void ConfigureValidatePublishedPolicyFragments(IServiceCollection services)
    {
        ConfigureGetFilePolicyFragments(services);
        ConfigureGetApimPolicyFragments(services);

        services.TryAddSingleton<ValidatePublishedPolicyFragmentsHandler>();
        services.TryAddSingleton<ValidatePublishedPolicyFragments>(provider => provider.GetRequiredService<ValidatePublishedPolicyFragmentsHandler>().Handle);
    }
}

internal static class PolicyFragment
{
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
