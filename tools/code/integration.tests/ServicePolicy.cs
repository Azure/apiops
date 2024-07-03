using Azure.Core.Pipeline;
using common;
using common.tests;
using CsCheck;
using FluentAssertions;
using LanguageExt;
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

internal delegate ValueTask DeleteAllServicePolicies(ManagementServiceName serviceName, CancellationToken cancellationToken);

internal delegate ValueTask PutServicePolicyModels(IEnumerable<ServicePolicyModel> models, ManagementServiceName serviceName, CancellationToken cancellationToken);

internal delegate ValueTask ValidateExtractedServicePolicies(ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

file delegate ValueTask<FrozenDictionary<ServicePolicyName, ServicePolicyDto>> GetApimServicePolicies(ManagementServiceName serviceName, CancellationToken cancellationToken);

file delegate ValueTask<FrozenDictionary<ServicePolicyName, ServicePolicyDto>> GetFileServicePolicies(ManagementServiceDirectory serviceDirectory, Option<CommitId> commitIdOption, CancellationToken cancellationToken);

internal delegate ValueTask WriteServicePolicyModels(IEnumerable<ServicePolicyModel> models, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

internal delegate ValueTask ValidatePublishedServicePolicies(IDictionary<ServicePolicyName, ServicePolicyDto> overrides, Option<CommitId> commitIdOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

file sealed class DeleteAllServicePoliciesHandler(ILogger<DeleteAllServicePolicies> logger, GetManagementServiceUri getServiceUri, HttpPipeline pipeline, ActivitySource activitySource)
{
    public async ValueTask Handle(ManagementServiceName serviceName, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(DeleteAllServicePolicies));

        logger.LogInformation("Deleting all service policies in {ServiceName}...", serviceName);
        var serviceUri = getServiceUri(serviceName);
        await ServicePoliciesUri.From(serviceUri).DeleteAll(pipeline, cancellationToken);
    }
}

file sealed class PutServicePolicyModelsHandler(ILogger<PutServicePolicyModels> logger, GetManagementServiceUri getServiceUri, HttpPipeline pipeline, ActivitySource activitySource)
{
    public async ValueTask Handle(IEnumerable<ServicePolicyModel> models, ManagementServiceName serviceName, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(PutServicePolicyModels));

        logger.LogInformation("Putting version set models in {ServiceName}...", serviceName);
        await models.IterParallel(async model =>
        {
            await Put(model, serviceName, cancellationToken);
        }, cancellationToken);
    }

    private async ValueTask Put(ServicePolicyModel model, ManagementServiceName serviceName, CancellationToken cancellationToken)
    {
        var serviceUri = getServiceUri(serviceName);
        var uri = ServicePolicyUri.From(model.Name, serviceUri);
        var dto = GetDto(model);

        await uri.PutDto(dto, pipeline, cancellationToken);
    }

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

file sealed class ValidateExtractedServicePoliciesHandler(ILogger<ValidateExtractedServicePolicies> logger, GetApimServicePolicies getApimResources, GetFileServicePolicies getFileResources, ActivitySource activitySource)
{
    public async ValueTask Handle(ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(ValidateExtractedServicePolicies));

        logger.LogInformation("Validating extracted service policies in {ServiceName}...", serviceName);
        var apimResources = await getApimResources(serviceName, cancellationToken);
        var fileResources = await getFileResources(serviceDirectory, Prelude.None, cancellationToken);

        var expected = apimResources.MapValue(NormalizeDto);
        var actual = fileResources.MapValue(NormalizeDto);

        actual.Should().BeEquivalentTo(expected);
    }

    private static string NormalizeDto(ServicePolicyDto dto) =>
        new
        {
            Value = new string((dto.Properties.Value ?? string.Empty)
                                .ReplaceLineEndings(string.Empty)
                                .Where(c => char.IsWhiteSpace(c) is false)
                                .ToArray())
        }.ToString()!;
}

file sealed class GetApimServicePoliciesHandler(ILogger<GetApimServicePolicies> logger, GetManagementServiceUri getServiceUri, HttpPipeline pipeline, ActivitySource activitySource)
{
    public async ValueTask<FrozenDictionary<ServicePolicyName, ServicePolicyDto>> Handle(ManagementServiceName serviceName, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(GetApimServicePolicies));

        logger.LogInformation("Getting service policies from {ServiceName}...", serviceName);

        var serviceUri = getServiceUri(serviceName);
        var uri = ServicePoliciesUri.From(serviceUri);

        return await uri.List(pipeline, cancellationToken)
                        .ToFrozenDictionary(cancellationToken);
    }
}

file sealed class GetFileServicePoliciesHandler(ILogger<GetFileServicePolicies> logger, ActivitySource activitySource)
{
    public async ValueTask<FrozenDictionary<ServicePolicyName, ServicePolicyDto>> Handle(ManagementServiceDirectory serviceDirectory, Option<CommitId> commitIdOption, CancellationToken cancellationToken) =>
        await commitIdOption.Map(commitId => GetWithCommit(serviceDirectory, commitId, cancellationToken))
                           .IfNone(() => GetWithoutCommit(serviceDirectory, cancellationToken));

    private async ValueTask<FrozenDictionary<ServicePolicyName, ServicePolicyDto>> GetWithCommit(ManagementServiceDirectory serviceDirectory, CommitId commitId, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(GetFileServicePolicies));

        logger.LogInformation("Getting service policies from {ServiceDirectory} as of commit {CommitId}...", serviceDirectory, commitId);

        return await Git.GetExistingFilesInCommit(serviceDirectory.ToDirectoryInfo(), commitId)
                        .ToAsyncEnumerable()
                        .Choose(file => ServicePolicyFile.TryParse(file, serviceDirectory))
                        .Choose(async file => await TryGetCommitResource(commitId, serviceDirectory, file, cancellationToken))
                        .ToFrozenDictionary(cancellationToken);
    }

    private static async ValueTask<Option<(ServicePolicyName name, ServicePolicyDto dto)>> TryGetCommitResource(CommitId commitId, ManagementServiceDirectory serviceDirectory, ServicePolicyFile file, CancellationToken cancellationToken)
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

    private async ValueTask<FrozenDictionary<ServicePolicyName, ServicePolicyDto>> GetWithoutCommit(ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(GetFileServicePolicies));

        logger.LogInformation("Getting service policies from {ServiceDirectory}...", serviceDirectory);

        return await ServicePolicyModule.ListPolicyFiles(serviceDirectory)
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

file sealed class WriteServicePolicyModelsHandler(ILogger<WriteServicePolicyModels> logger, ActivitySource activitySource)
{
    public async ValueTask Handle(IEnumerable<ServicePolicyModel> models, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(WriteServicePolicyModels));

        logger.LogInformation("Writing version set models to {ServiceDirectory}...", serviceDirectory);
        await models.IterParallel(async model =>
        {
            await WritePolicyFile(model, serviceDirectory, cancellationToken);
        }, cancellationToken);
    }

    private static async ValueTask WritePolicyFile(ServicePolicyModel model, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        var policyFile = ServicePolicyFile.From(model.Name, serviceDirectory);
        await policyFile.WritePolicy(model.Content, cancellationToken);
    }

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

file sealed class ValidatePublishedServicePoliciesHandler(ILogger<ValidatePublishedServicePolicies> logger, GetFileServicePolicies getFileResources, GetApimServicePolicies getApimResources, ActivitySource activitySource)
{
    public async ValueTask Handle(IDictionary<ServicePolicyName, ServicePolicyDto> overrides, Option<CommitId> commitIdOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(ValidatePublishedServicePolicies));

        logger.LogInformation("Validating published service policies in {ServiceDirectory}...", serviceDirectory);

        var apimResources = await getApimResources(serviceName, cancellationToken);
        var fileResources = await getFileResources(serviceDirectory, commitIdOption, cancellationToken);

        var expected = PublisherOptions.Override(fileResources, overrides)
                                       .MapValue(NormalizeDto);
        var actual = apimResources.MapValue(NormalizeDto);

        actual.Should().BeEquivalentTo(expected);
    }

    private static string NormalizeDto(ServicePolicyDto dto) =>
        new
        {
            Value = new string((dto.Properties.Value ?? string.Empty)
                                .ReplaceLineEndings(string.Empty)
                                .Where(c => char.IsWhiteSpace(c) is false)
                                .ToArray())
        }.ToString()!;
}

internal static class ServicePolicyServices
{
    public static void ConfigureDeleteAllServicePolicies(IServiceCollection services)
    {
        ManagementServices.ConfigureGetManagementServiceUri(services);

        services.TryAddSingleton<DeleteAllServicePoliciesHandler>();
        services.TryAddSingleton<DeleteAllServicePolicies>(provider => provider.GetRequiredService<DeleteAllServicePoliciesHandler>().Handle);
    }

    public static void ConfigurePutServicePolicyModels(IServiceCollection services)
    {
        ManagementServices.ConfigureGetManagementServiceUri(services);

        services.TryAddSingleton<PutServicePolicyModelsHandler>();
        services.TryAddSingleton<PutServicePolicyModels>(provider => provider.GetRequiredService<PutServicePolicyModelsHandler>().Handle);
    }

    public static void ConfigureValidateExtractedServicePolicies(IServiceCollection services)
    {
        ConfigureGetApimServicePolicies(services);
        ConfigureGetFileServicePolicies(services);

        services.TryAddSingleton<ValidateExtractedServicePoliciesHandler>();
        services.TryAddSingleton<ValidateExtractedServicePolicies>(provider => provider.GetRequiredService<ValidateExtractedServicePoliciesHandler>().Handle);
    }

    private static void ConfigureGetApimServicePolicies(IServiceCollection services)
    {
        ManagementServices.ConfigureGetManagementServiceUri(services);

        services.TryAddSingleton<GetApimServicePoliciesHandler>();
        services.TryAddSingleton<GetApimServicePolicies>(provider => provider.GetRequiredService<GetApimServicePoliciesHandler>().Handle);
    }

    private static void ConfigureGetFileServicePolicies(IServiceCollection services)
    {
        services.TryAddSingleton<GetFileServicePoliciesHandler>();
        services.TryAddSingleton<GetFileServicePolicies>(provider => provider.GetRequiredService<GetFileServicePoliciesHandler>().Handle);
    }

    public static void ConfigureWriteServicePolicyModels(IServiceCollection services)
    {
        services.TryAddSingleton<WriteServicePolicyModelsHandler>();
        services.TryAddSingleton<WriteServicePolicyModels>(provider => provider.GetRequiredService<WriteServicePolicyModelsHandler>().Handle);
    }

    public static void ConfigureValidatePublishedServicePolicies(IServiceCollection services)
    {
        ConfigureGetFileServicePolicies(services);
        ConfigureGetApimServicePolicies(services);

        services.TryAddSingleton<ValidatePublishedServicePoliciesHandler>();
        services.TryAddSingleton<ValidatePublishedServicePolicies>(provider => provider.GetRequiredService<ValidatePublishedServicePoliciesHandler>().Handle);
    }
}

internal static class ServicePolicy
{
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
