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

public delegate ValueTask DeleteAllNamedValues(ManagementServiceName serviceName, CancellationToken cancellationToken);
public delegate ValueTask PutNamedValueModels(IEnumerable<NamedValueModel> models, ManagementServiceName serviceName, CancellationToken cancellationToken);
public delegate ValueTask ValidateExtractedNamedValues(Option<FrozenSet<NamedValueName>> namedvalueNamesOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);
public delegate ValueTask<FrozenDictionary<NamedValueName, NamedValueDto>> GetApimNamedValues(ManagementServiceName serviceName, CancellationToken cancellationToken);
public delegate ValueTask<FrozenDictionary<NamedValueName, NamedValueDto>> GetFileNamedValues(ManagementServiceDirectory serviceDirectory, Option<CommitId> commitIdOption, CancellationToken cancellationToken);
public delegate ValueTask WriteNamedValueModels(IEnumerable<NamedValueModel> models, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);
public delegate ValueTask ValidatePublishedNamedValues(IDictionary<NamedValueName, NamedValueDto> overrides, Option<CommitId> commitIdOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

public static class NamedValueModule
{
    public static void ConfigureDeleteAllNamedValues(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureGetManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteAllNamedValues);
    }

    private static DeleteAllNamedValues GetDeleteAllNamedValues(IServiceProvider provider)
    {
        var getServiceUri = provider.GetRequiredService<GetManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (serviceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteAllNamedValues));

            logger.LogInformation("Deleting all named values in {ServiceName}...", serviceName);

            var serviceUri = getServiceUri(serviceName);

            await NamedValuesUri.From(serviceUri)
                                .DeleteAll(pipeline, cancellationToken);
        };
    }

    public static void ConfigurePutNamedValueModels(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureGetManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutNamedValueModels);
    }

    private static PutNamedValueModels GetPutNamedValueModels(IServiceProvider provider)
    {
        var getServiceUri = provider.GetRequiredService<GetManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (models, serviceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutNamedValueModels));

            logger.LogInformation("Putting named value models in {ServiceName}...", serviceName);

            await models.IterParallel(async model =>
            {
                await put(model, serviceName, cancellationToken);
            }, cancellationToken);
        };

        async ValueTask put(NamedValueModel model, ManagementServiceName serviceName, CancellationToken cancellationToken)
        {
            var serviceUri = getServiceUri(serviceName);

            var dto = getDto(model);

            await NamedValueUri.From(model.Name, serviceUri)
                               .PutDto(dto, pipeline, cancellationToken);
        }

        static NamedValueDto getDto(NamedValueModel model) =>
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

    public static void ConfigureValidateExtractedNamedValues(IHostApplicationBuilder builder)
    {
        ConfigureGetApimNamedValues(builder);
        ConfigureGetFileNamedValues(builder);

        builder.Services.TryAddSingleton(GetValidateExtractedNamedValues);
    }

    private static ValidateExtractedNamedValues GetValidateExtractedNamedValues(IServiceProvider provider)
    {
        var getApimResources = provider.GetRequiredService<GetApimNamedValues>();
        var tryGetApimGraphQlSchema = provider.GetRequiredService<TryGetApimGraphQlSchema>();
        var getFileResources = provider.GetRequiredService<GetFileNamedValues>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (namesFilterOption, serviceName, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ValidateExtractedNamedValues));

            logger.LogInformation("Validating extracted named values in {ServiceName}...", serviceName);

            var apimResources = await getApimResources(serviceName, cancellationToken);
            var fileResources = await getFileResources(serviceDirectory, Prelude.None, cancellationToken);

            var expected = apimResources.WhereKey(name => ExtractorOptions.ShouldExtract(name, namesFilterOption))
                                        .MapValue(normalizeDto)
                                        .ToFrozenDictionary();

            var actual = fileResources.MapValue(normalizeDto)
                                      .ToFrozenDictionary();

            actual.Should().BeEquivalentTo(expected);
        };

        static string normalizeDto(NamedValueDto dto) =>
            new
            {
                DisplayName = dto.Properties.DisplayName ?? string.Empty,
                Tags = string.Join(',', (dto.Properties.Tags ?? []).Order()),
                Value = dto.Properties.Secret is true ? string.Empty : dto.Properties.Value ?? string.Empty,
                Secret = dto.Properties.Secret ?? false
            }.ToString()!;
    }

    public static void ConfigureGetApimNamedValues(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureGetManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetGetApimNamedValues);
    }

    private static GetApimNamedValues GetGetApimNamedValues(IServiceProvider provider)
    {
        var getServiceUri = provider.GetRequiredService<GetManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (serviceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(GetApimNamedValues));

            logger.LogInformation("Getting named values from {ServiceName}...", serviceName);

            var serviceUri = getServiceUri(serviceName);

            return await NamedValuesUri.From(serviceUri)
                                       .List(pipeline, cancellationToken)
                                       .ToFrozenDictionary(cancellationToken);
        };
    }

    public static void ConfigureGetFileNamedValues(IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(GetGetFileNamedValues);
    }

    private static GetFileNamedValues GetGetFileNamedValues(IServiceProvider provider)
    {
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (serviceDirectory, commitIdOption, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(GetFileNamedValues));

            return await commitIdOption.Map(commitId => getWithCommit(serviceDirectory, commitId, cancellationToken))
                                       .IfNone(() => getWithoutCommit(serviceDirectory, cancellationToken));
        };

        async ValueTask<FrozenDictionary<NamedValueName, NamedValueDto>> getWithCommit(ManagementServiceDirectory serviceDirectory, CommitId commitId, CancellationToken cancellationToken)
        {
            using var _ = activitySource.StartActivity(nameof(GetFileNamedValues));

            logger.LogInformation("Getting named values from {ServiceDirectory} as of commit {CommitId}...", serviceDirectory, commitId);

            return await Git.GetExistingFilesInCommit(serviceDirectory.ToDirectoryInfo(), commitId)
                            .ToAsyncEnumerable()
                            .Choose(file => NamedValueInformationFile.TryParse(file, serviceDirectory))
                            .Choose(async file => await tryGetCommitResource(commitId, serviceDirectory, file, cancellationToken))
                            .ToFrozenDictionary(cancellationToken);
        }

        static async ValueTask<Option<(NamedValueName name, NamedValueDto dto)>> tryGetCommitResource(CommitId commitId, ManagementServiceDirectory serviceDirectory, NamedValueInformationFile file, CancellationToken cancellationToken)
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

        async ValueTask<FrozenDictionary<NamedValueName, NamedValueDto>> getWithoutCommit(ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
        {
            logger.LogInformation("Getting named values from {ServiceDirectory}...", serviceDirectory);

            return await common.NamedValueModule.ListInformationFiles(serviceDirectory)
                                                .ToAsyncEnumerable()
                                                .SelectAwait(async file => (file.Parent.Name,
                                                                            await file.ReadDto(cancellationToken)))
                                                .ToFrozenDictionary(cancellationToken);
        }
    }

    public static void ConfigureWriteNamedValueModels(IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(GetWriteNamedValueModels);
    }

    private static WriteNamedValueModels GetWriteNamedValueModels(IServiceProvider provider)
    {
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (models, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(WriteNamedValueModels));

            logger.LogInformation("Writing named value models to {ServiceDirectory}...", serviceDirectory);

            await models.IterParallel(async model =>
            {
                await writeInformationFile(model, serviceDirectory, cancellationToken);
            }, cancellationToken);
        };

        static async ValueTask writeInformationFile(NamedValueModel model, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
        {
            var informationFile = NamedValueInformationFile.From(model.Name, serviceDirectory);
            var dto = getDto(model);

            await informationFile.WriteDto(dto, cancellationToken);
        }

        static NamedValueDto getDto(NamedValueModel model) =>
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

    public static void ConfigureValidatePublishedNamedValues(IHostApplicationBuilder builder)
    {
        ConfigureGetFileNamedValues(builder);
        ConfigureGetApimNamedValues(builder);

        builder.Services.TryAddSingleton(GetValidatePublishedNamedValues);
    }

    private static ValidatePublishedNamedValues GetValidatePublishedNamedValues(IServiceProvider provider)
    {
        var getFileResources = provider.GetRequiredService<GetFileNamedValues>();
        var getApimResources = provider.GetRequiredService<GetApimNamedValues>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (overrides, commitIdOption, serviceName, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ValidatePublishedNamedValues));

            logger.LogInformation("Validating published named values in {ServiceDirectory}...", serviceDirectory);

            var apimResources = await getApimResources(serviceName, cancellationToken);
            var fileResources = await getFileResources(serviceDirectory, commitIdOption, cancellationToken);

            var expected = PublisherOptions.Override(fileResources, overrides)
                                           .WhereValue(dto => (dto.Properties.Secret ?? false) is false)
                                           .MapValue(normalizeDto)
                                           .ToFrozenDictionary();

            var actual = apimResources.WhereValue(dto => (dto.Properties.Secret ?? false) is false)
                                      .MapValue(normalizeDto)
                                      .ToFrozenDictionary();

            actual.Should().BeEquivalentTo(expected);
        };

        static string normalizeDto(NamedValueDto dto) =>
            new
            {
                DisplayName = dto.Properties.DisplayName ?? string.Empty,
                Tags = string.Join(',', (dto.Properties.Tags ?? []).Order()),
                Value = dto.Properties.Secret is true ? string.Empty : dto.Properties.Value ?? string.Empty,
                Secret = dto.Properties.Secret ?? false
            }.ToString()!;
    }

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