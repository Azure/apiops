using Azure.Core.Pipeline;
using common;
using common.tests;
using CsCheck;
using FluentAssertions;
using LanguageExt;
using LanguageExt.UnsafeValueAccess;
using publisher;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace integration.tests;

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
        models.ToFrozenDictionary(model => model.Name, GetPublisherDto);

    private static NamedValueDto GetPublisherDto(NamedValueModel model) =>
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

    public static async ValueTask Put(IEnumerable<NamedValueModel> models, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await models.IterParallel(async model =>
        {
            await Put(model, serviceUri, pipeline, cancellationToken);
        }, cancellationToken);

    private static async ValueTask Put(NamedValueModel model, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var uri = NamedValueUri.From(model.Name, serviceUri);
        var dto = GetPublisherDto(model);

        await uri.PutDto(dto, pipeline, cancellationToken);
    }

    public static async ValueTask DeleteAll(ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await NamedValuesUri.From(serviceUri).DeleteAll(pipeline, cancellationToken);

    public static async ValueTask WriteArtifacts(IEnumerable<NamedValueModel> models, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await models.IterParallel(async model =>
        {
            await WriteInformationFile(model, serviceDirectory, cancellationToken);
        }, cancellationToken);

    private static async ValueTask WriteInformationFile(NamedValueModel model, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        var informationFile = NamedValueInformationFile.From(model.Name, serviceDirectory);
        var dto = GetExtractorDto(model);

        await informationFile.WriteDto(dto, cancellationToken);
    }

    private static NamedValueDto GetExtractorDto(NamedValueModel model) =>
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

    public static async ValueTask ValidateExtractedArtifacts(Option<FrozenSet<NamedValueName>> namesToExtract, ManagementServiceDirectory serviceDirectory, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var apimResources = await GetApimResources(serviceUri, pipeline, cancellationToken);
        var fileResources = await GetFileResources(serviceDirectory, cancellationToken);

        var expected = apimResources.WhereKey(name => ExtractorOptions.ShouldExtract(name, namesToExtract))
                                    .MapValue(NormalizeDto);
        var actual = fileResources.MapValue(NormalizeDto);

        actual.Should().BeEquivalentTo(expected);
    }

    private static async ValueTask<FrozenDictionary<NamedValueName, NamedValueDto>> GetApimResources(ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var uri = NamedValuesUri.From(serviceUri);

        return await uri.List(pipeline, cancellationToken)
                        .ToFrozenDictionary(cancellationToken);
    }

    private static async ValueTask<FrozenDictionary<NamedValueName, NamedValueDto>> GetFileResources(ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await NamedValueModule.ListInformationFiles(serviceDirectory)
                              .ToAsyncEnumerable()
                              .SelectAwait(async file => (file.Parent.Name,
                                                          await file.ReadDto(cancellationToken)))
                              .ToFrozenDictionary(cancellationToken);

    private static string NormalizeDto(NamedValueDto dto) =>
        new
        {
            DisplayName = dto.Properties.DisplayName ?? string.Empty,
            Tags = string.Join(',', (dto.Properties.Tags ?? []).Order()),
            Value = dto.Properties.Secret is true ? string.Empty : dto.Properties.Value ?? string.Empty,
            Secret = dto.Properties.Secret
        }.ToString()!;

    public static async ValueTask ValidatePublisherChanges(ManagementServiceDirectory serviceDirectory, IDictionary<NamedValueName, NamedValueDto> overrides, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var fileResources = await GetFileResources(serviceDirectory, cancellationToken);
        await ValidatePublisherChanges(fileResources, overrides, serviceUri, pipeline, cancellationToken);
    }

    private static async ValueTask ValidatePublisherChanges(IDictionary<NamedValueName, NamedValueDto> fileResources, IDictionary<NamedValueName, NamedValueDto> overrides, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var apimResources = await GetApimResources(serviceUri, pipeline, cancellationToken);

        var expected = PublisherOptions.Override(fileResources, overrides)
                                       .WhereValue(dto => (dto.Properties.Secret is true
                                                            && dto.Properties.Value is null
                                                            && dto.Properties.KeyVault?.SecretIdentifier is null) is false)
                                       .MapValue(NormalizeDto);
        var actual = apimResources.MapValue(NormalizeDto);
        actual.Should().BeEquivalentTo(expected);
    }

    public static async ValueTask ValidatePublisherCommitChanges(CommitId commitId, ManagementServiceDirectory serviceDirectory, IDictionary<NamedValueName, NamedValueDto> overrides, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var fileResources = await GetFileResources(commitId, serviceDirectory, cancellationToken);
        await ValidatePublisherChanges(fileResources, overrides, serviceUri, pipeline, cancellationToken);
    }

    private static async ValueTask<FrozenDictionary<NamedValueName, NamedValueDto>> GetFileResources(CommitId commitId, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await Git.GetExistingFilesInCommit(serviceDirectory.ToDirectoryInfo(), commitId)
                 .ToAsyncEnumerable()
                 .Choose(file => NamedValueInformationFile.TryParse(file, serviceDirectory))
                 .Choose(async file => await TryGetCommitResource(commitId, serviceDirectory, file, cancellationToken))
                 .ToFrozenDictionary(cancellationToken);

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
}
