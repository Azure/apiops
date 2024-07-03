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

internal delegate ValueTask DeleteAllNamedValues(ManagementServiceName serviceName, CancellationToken cancellationToken);

internal delegate ValueTask PutNamedValueModels(IEnumerable<NamedValueModel> models, ManagementServiceName serviceName, CancellationToken cancellationToken);

internal delegate ValueTask ValidateExtractedNamedValues(Option<FrozenSet<NamedValueName>> namesFilterOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

file delegate ValueTask<FrozenDictionary<NamedValueName, NamedValueDto>> GetApimNamedValues(ManagementServiceName serviceName, CancellationToken cancellationToken);

file delegate ValueTask<FrozenDictionary<NamedValueName, NamedValueDto>> GetFileNamedValues(ManagementServiceDirectory serviceDirectory, Option<CommitId> commitIdOption, CancellationToken cancellationToken);

internal delegate ValueTask WriteNamedValueModels(IEnumerable<NamedValueModel> models, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

internal delegate ValueTask ValidatePublishedNamedValues(IDictionary<NamedValueName, NamedValueDto> overrides, Option<CommitId> commitIdOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

file sealed class DeleteAllNamedValuesHandler(ILogger<DeleteAllNamedValues> logger, GetManagementServiceUri getServiceUri, HttpPipeline pipeline, ActivitySource activitySource)
{
    public async ValueTask Handle(ManagementServiceName serviceName, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(DeleteAllNamedValues));

        logger.LogInformation("Deleting all named values in {ServiceName}...", serviceName);
        var serviceUri = getServiceUri(serviceName);
        await NamedValuesUri.From(serviceUri).DeleteAll(pipeline, cancellationToken);
    }
}

file sealed class PutNamedValueModelsHandler(ILogger<PutNamedValueModels> logger, GetManagementServiceUri getServiceUri, HttpPipeline pipeline, ActivitySource activitySource)
{
    public async ValueTask Handle(IEnumerable<NamedValueModel> models, ManagementServiceName serviceName, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(PutNamedValueModels));

        logger.LogInformation("Putting named value models in {ServiceName}...", serviceName);
        await models.IterParallel(async model =>
        {
            await Put(model, serviceName, cancellationToken);
        }, cancellationToken);
    }

    private async ValueTask Put(NamedValueModel model, ManagementServiceName serviceName, CancellationToken cancellationToken)
    {
        var serviceUri = getServiceUri(serviceName);
        var uri = NamedValueUri.From(model.Name, serviceUri);
        var dto = GetDto(model);

        await uri.PutDto(dto, pipeline, cancellationToken);
    }

    private static NamedValueDto GetDto(NamedValueModel model) =>
        new()
        {
            Properties = new NamedValueDto.NamedValueContract
            {
                DisplayName = model.Name.ToString(),
                Tags = model.Tags,
                KeyVault = model.Type switch
                {
                    NamedValueType.KeyVault keyVault => new NamedValueDto.KeyVaultContract
                    {
                        SecretIdentifier = keyVault.SecretIdentifier,
                        IdentityClientId = keyVault.IdentityClientId.ValueUnsafe()
                    },
                    _ => null
                },
                Secret = model.Type is NamedValueType.Secret or NamedValueType.KeyVault,
                Value = model.Type switch
                {
                    NamedValueType.Secret secret => secret.Value,
                    NamedValueType.Default @default => @default.Value,
                    _ => null
                }
            }
        };
}

file sealed class ValidateExtractedNamedValuesHandler(ILogger<ValidateExtractedNamedValues> logger, GetApimNamedValues getApimResources, GetFileNamedValues getFileResources, ActivitySource activitySource)
{
    public async ValueTask Handle(Option<FrozenSet<NamedValueName>> namesFilterOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(ValidateExtractedNamedValues));

        logger.LogInformation("Validating extracted named values in {ServiceName}...", serviceName);
        var apimResources = await getApimResources(serviceName, cancellationToken);
        var fileResources = await getFileResources(serviceDirectory, Prelude.None, cancellationToken);

        var expected = apimResources.WhereKey(name => ExtractorOptions.ShouldExtract(name, namesFilterOption))
                                    .MapValue(NormalizeDto);
        var actual = fileResources.MapValue(NormalizeDto);

        actual.Should().BeEquivalentTo(expected);
    }

    private static string NormalizeDto(NamedValueDto dto) =>
        new
        {
            DisplayName = dto.Properties.DisplayName ?? string.Empty,
            Tags = string.Join(',', (dto.Properties.Tags ?? []).Order()),
            Value = dto.Properties.Secret is true ? string.Empty : dto.Properties.Value ?? string.Empty,
            Secret = dto.Properties.Secret
        }.ToString()!;
}

file sealed class GetApimNamedValuesHandler(ILogger<GetApimNamedValues> logger, GetManagementServiceUri getServiceUri, HttpPipeline pipeline, ActivitySource activitySource)
{
    public async ValueTask<FrozenDictionary<NamedValueName, NamedValueDto>> Handle(ManagementServiceName serviceName, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(GetApimNamedValues));

        logger.LogInformation("Getting named values from {ServiceName}...", serviceName);

        var serviceUri = getServiceUri(serviceName);
        var uri = NamedValuesUri.From(serviceUri);

        return await uri.List(pipeline, cancellationToken)
                        .ToFrozenDictionary(cancellationToken);
    }
}

file sealed class GetFileNamedValuesHandler(ILogger<GetFileNamedValues> logger, ActivitySource activitySource)
{
    public async ValueTask<FrozenDictionary<NamedValueName, NamedValueDto>> Handle(ManagementServiceDirectory serviceDirectory, Option<CommitId> commitIdOption, CancellationToken cancellationToken) =>
        await commitIdOption.Map(commitId => GetWithCommit(serviceDirectory, commitId, cancellationToken))
                           .IfNone(() => GetWithoutCommit(serviceDirectory, cancellationToken));

    private async ValueTask<FrozenDictionary<NamedValueName, NamedValueDto>> GetWithCommit(ManagementServiceDirectory serviceDirectory, CommitId commitId, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(GetFileNamedValues));

        logger.LogInformation("Getting named values from {ServiceDirectory} as of commit {CommitId}...", serviceDirectory, commitId);

        return await Git.GetExistingFilesInCommit(serviceDirectory.ToDirectoryInfo(), commitId)
                        .ToAsyncEnumerable()
                        .Choose(file => NamedValueInformationFile.TryParse(file, serviceDirectory))
                        .Choose(async file => await TryGetCommitResource(commitId, serviceDirectory, file, cancellationToken))
                        .ToFrozenDictionary(cancellationToken);
    }

    private static async ValueTask<Option<(NamedValueName name, NamedValueDto dto)>> TryGetCommitResource(CommitId commitId, ManagementServiceDirectory serviceDirectory, NamedValueInformationFile file, CancellationToken cancellationToken)
    {
        var name = file.Parent.Name;
        var contentsOption = Git.TryGetFileContentsInCommit(serviceDirectory.ToDirectoryInfo(), file.ToFileInfo(), commitId);

        return await contentsOption.MapTask(async contents =>
        {
            using (contents)
            {
                var data = await BinaryData.FromStreamAsync(contents, cancellationToken);
                var dto = data.ToObjectFromJson<NamedValueDto>();
                return (name, dto);
            }
        });
    }

    private async ValueTask<FrozenDictionary<NamedValueName, NamedValueDto>> GetWithoutCommit(ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(GetFileNamedValues));

        logger.LogInformation("Getting named values from {ServiceDirectory}...", serviceDirectory);

        return await NamedValueModule.ListInformationFiles(serviceDirectory)
                                     .ToAsyncEnumerable()
                                     .SelectAwait(async file => (file.Parent.Name,
                                                                 await file.ReadDto(cancellationToken)))
                                     .ToFrozenDictionary(cancellationToken);
    }
}

file sealed class WriteNamedValueModelsHandler(ILogger<WriteNamedValueModels> logger, ActivitySource activitySource)
{
    public async ValueTask Handle(IEnumerable<NamedValueModel> models, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(WriteNamedValueModels));

        logger.LogInformation("Writing named value models to {ServiceDirectory}...", serviceDirectory);
        await models.IterParallel(async model =>
        {
            await WriteInformationFile(model, serviceDirectory, cancellationToken);
        }, cancellationToken);
    }

    private static async ValueTask WriteInformationFile(NamedValueModel model, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        var informationFile = NamedValueInformationFile.From(model.Name, serviceDirectory);
        var dto = GetDto(model);

        await informationFile.WriteDto(dto, cancellationToken);
    }

    private static NamedValueDto GetDto(NamedValueModel model) =>
        new()
        {
            Properties = new NamedValueDto.NamedValueContract
            {
                DisplayName = model.Name.ToString(),
                Tags = model.Tags,
                KeyVault = model.Type switch
                {
                    NamedValueType.KeyVault keyVault => new NamedValueDto.KeyVaultContract
                    {
                        SecretIdentifier = keyVault.SecretIdentifier,
                        IdentityClientId = keyVault.IdentityClientId.ValueUnsafe()
                    },
                    _ => null
                },
                Secret = model.Type is NamedValueType.Secret or NamedValueType.KeyVault,
                Value = model.Type is NamedValueType.Default @default ? @default.Value : null
            }
        };
}

file sealed class ValidatePublishedNamedValuesHandler(ILogger<ValidatePublishedNamedValues> logger, GetFileNamedValues getFileResources, GetApimNamedValues getApimResources, ActivitySource activitySource)
{
    public async ValueTask Handle(IDictionary<NamedValueName, NamedValueDto> overrides, Option<CommitId> commitIdOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(ValidatePublishedNamedValues));

        logger.LogInformation("Validating published named values in {ServiceDirectory}...", serviceDirectory);

        var apimResources = await getApimResources(serviceName, cancellationToken);
        var fileResources = await getFileResources(serviceDirectory, commitIdOption, cancellationToken);

        var expected = PublisherOptions.Override(fileResources, overrides)
                                       .WhereValue(dto => (dto.Properties.Secret is true
                                                            && dto.Properties.Value is null
                                                            && dto.Properties.KeyVault?.SecretIdentifier is null) is false)
                                       .MapValue(NormalizeDto);
        var actual = apimResources.MapValue(NormalizeDto);

        actual.Should().BeEquivalentTo(expected);
    }

    private static string NormalizeDto(NamedValueDto dto) =>
        new
        {
            DisplayName = dto.Properties.DisplayName ?? string.Empty,
            Tags = string.Join(',', (dto.Properties.Tags ?? []).Order()),
            Value = dto.Properties.Secret is true ? string.Empty : dto.Properties.Value ?? string.Empty,
            Secret = dto.Properties.Secret
        }.ToString()!;
}

internal static class NamedValueServices
{
    public static void ConfigureDeleteAllNamedValues(IServiceCollection services)
    {
        ManagementServices.ConfigureGetManagementServiceUri(services);

        services.TryAddSingleton<DeleteAllNamedValuesHandler>();
        services.TryAddSingleton<DeleteAllNamedValues>(provider => provider.GetRequiredService<DeleteAllNamedValuesHandler>().Handle);
    }

    public static void ConfigurePutNamedValueModels(IServiceCollection services)
    {
        ManagementServices.ConfigureGetManagementServiceUri(services);

        services.TryAddSingleton<PutNamedValueModelsHandler>();
        services.TryAddSingleton<PutNamedValueModels>(provider => provider.GetRequiredService<PutNamedValueModelsHandler>().Handle);
    }

    public static void ConfigureValidateExtractedNamedValues(IServiceCollection services)
    {
        ConfigureGetApimNamedValues(services);
        ConfigureGetFileNamedValues(services);

        services.TryAddSingleton<ValidateExtractedNamedValuesHandler>();
        services.TryAddSingleton<ValidateExtractedNamedValues>(provider => provider.GetRequiredService<ValidateExtractedNamedValuesHandler>().Handle);
    }

    private static void ConfigureGetApimNamedValues(IServiceCollection services)
    {
        ManagementServices.ConfigureGetManagementServiceUri(services);

        services.TryAddSingleton<GetApimNamedValuesHandler>();
        services.TryAddSingleton<GetApimNamedValues>(provider => provider.GetRequiredService<GetApimNamedValuesHandler>().Handle);
    }

    private static void ConfigureGetFileNamedValues(IServiceCollection services)
    {
        services.TryAddSingleton<GetFileNamedValuesHandler>();
        services.TryAddSingleton<GetFileNamedValues>(provider => provider.GetRequiredService<GetFileNamedValuesHandler>().Handle);
    }

    public static void ConfigureWriteNamedValueModels(IServiceCollection services)
    {
        services.TryAddSingleton<WriteNamedValueModelsHandler>();
        services.TryAddSingleton<WriteNamedValueModels>(provider => provider.GetRequiredService<WriteNamedValueModelsHandler>().Handle);
    }

    public static void ConfigureValidatePublishedNamedValues(IServiceCollection services)
    {
        ConfigureGetFileNamedValues(services);
        ConfigureGetApimNamedValues(services);

        services.TryAddSingleton<ValidatePublishedNamedValuesHandler>();
        services.TryAddSingleton<ValidatePublishedNamedValues>(provider => provider.GetRequiredService<ValidatePublishedNamedValuesHandler>().Handle);
    }
}

internal static class NamedValue
{
    public static Gen<NamedValueModel> GenerateUpdate(NamedValueModel original) =>
        from tags in NamedValueModel.GenerateTags()
        select original with
        {
            Tags = tags
        };

    public static Gen<NamedValueDto> GenerateOverride(NamedValueDto original) =>
        from value in Generator.AlphaNumericStringBetween(1, 100)
        from tags in NamedValueModel.GenerateTags()
        select new NamedValueDto
        {
            Properties = new NamedValueDto.NamedValueContract
            {
                Value = value,
                Tags = tags
            }
        };

    public static FrozenDictionary<NamedValueName, NamedValueDto> GetDtoDictionary(IEnumerable<NamedValueModel> models) =>
        models.ToFrozenDictionary(model => model.Name, GetDto);

    private static NamedValueDto GetDto(NamedValueModel model) =>
        new()
        {
            Properties = new NamedValueDto.NamedValueContract
            {
                DisplayName = model.Name.ToString(),
                Tags = model.Tags,
                KeyVault = model.Type switch
                {
                    NamedValueType.KeyVault keyVault => new NamedValueDto.KeyVaultContract
                    {
                        SecretIdentifier = keyVault.SecretIdentifier,
                        IdentityClientId = keyVault.IdentityClientId.ValueUnsafe()
                    },
                    _ => null
                },
                Secret = model.Type is NamedValueType.Secret or NamedValueType.KeyVault,
                Value = model.Type switch
                {
                    NamedValueType.Secret secret => secret.Value,
                    NamedValueType.Default @default => @default.Value,
                    _ => null
                }
            }
        };
}
