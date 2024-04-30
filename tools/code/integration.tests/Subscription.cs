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

internal static class Subscription
{
    public static Gen<SubscriptionModel> GenerateUpdate(SubscriptionModel original) =>
        from displayName in SubscriptionModel.GenerateDisplayName()
        from allowTracing in Gen.Bool.OptionOf()
        select original with
        {
            DisplayName = displayName,
            AllowTracing = allowTracing
        };

    public static Gen<SubscriptionDto> GenerateOverride(SubscriptionDto original) =>
        from displayName in SubscriptionModel.GenerateDisplayName()
        from allowTracing in Gen.Bool.OptionOf()
        select new SubscriptionDto
        {
            Properties = new SubscriptionDto.SubscriptionContract
            {
                DisplayName = displayName,
                AllowTracing = allowTracing.ValueUnsafe()
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
                },
                AllowTracing = model.AllowTracing.ValueUnsafe()
            }
        };

    public static async ValueTask Put(IEnumerable<SubscriptionModel> models, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await models.IterParallel(async model =>
        {
            await Put(model, serviceUri, pipeline, cancellationToken);
        }, cancellationToken);

    private static async ValueTask Put(SubscriptionModel model, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var uri = SubscriptionUri.From(model.Name, serviceUri);
        var dto = GetDto(model);

        await uri.PutDto(dto, pipeline, cancellationToken);
    }

    public static async ValueTask DeleteAll(ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await SubscriptionsUri.From(serviceUri).DeleteAll(pipeline, cancellationToken);

    public static async ValueTask WriteArtifacts(IEnumerable<SubscriptionModel> models, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await models.IterParallel(async model =>
        {
            await WriteInformationFile(model, serviceDirectory, cancellationToken);
        }, cancellationToken);

    private static async ValueTask WriteInformationFile(SubscriptionModel model, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        var informationFile = SubscriptionInformationFile.From(model.Name, serviceDirectory);
        var dto = GetDto(model);

        await informationFile.WriteDto(dto, cancellationToken);
    }

    public static async ValueTask ValidateExtractedArtifacts(Option<FrozenSet<SubscriptionName>> namesToExtract, ManagementServiceDirectory serviceDirectory, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var apimResources = await GetApimResources(serviceUri, pipeline, cancellationToken);
        var fileResources = await GetFileResources(serviceDirectory, cancellationToken);

        var expected = apimResources.WhereKey(name => ExtractorOptions.ShouldExtract(name, namesToExtract))
                                    .MapValue(NormalizeDto);
        var actual = fileResources.MapValue(NormalizeDto);

        actual.Should().BeEquivalentTo(expected);
    }

    private static async ValueTask<FrozenDictionary<SubscriptionName, SubscriptionDto>> GetApimResources(ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var uri = SubscriptionsUri.From(serviceUri);

        return await uri.List(pipeline, cancellationToken)
                        .ToFrozenDictionary(cancellationToken);
    }

    private static async ValueTask<FrozenDictionary<SubscriptionName, SubscriptionDto>> GetFileResources(ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await SubscriptionModule.ListInformationFiles(serviceDirectory)
                              .ToAsyncEnumerable()
                              .SelectAwait(async file => (file.Parent.Name,
                                                          await file.ReadDto(cancellationToken)))
                              .ToFrozenDictionary(cancellationToken);

    private static string NormalizeDto(SubscriptionDto dto) =>
        new
        {
            DisplayName = dto.Properties.DisplayName ?? string.Empty,
            Scope = string.Join('/', dto.Properties.Scope?.Split('/')?.TakeLast(2)?.ToArray() ?? []),
            AllowTracing = dto.Properties.AllowTracing ?? false
        }.ToString()!;

    public static async ValueTask ValidatePublisherChanges(ManagementServiceDirectory serviceDirectory, IDictionary<SubscriptionName, SubscriptionDto> overrides, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var fileResources = await GetFileResources(serviceDirectory, cancellationToken);
        await ValidatePublisherChanges(fileResources, overrides, serviceUri, pipeline, cancellationToken);
    }

    private static async ValueTask ValidatePublisherChanges(IDictionary<SubscriptionName, SubscriptionDto> fileResources, IDictionary<SubscriptionName, SubscriptionDto> overrides, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var apimResources = await GetApimResources(serviceUri, pipeline, cancellationToken);

        var expected = PublisherOptions.Override(fileResources, overrides)
                                       .MapValue(NormalizeDto);
        var actual = apimResources.MapValue(NormalizeDto);
        actual.Should().BeEquivalentTo(expected);
    }

    public static async ValueTask ValidatePublisherCommitChanges(CommitId commitId, ManagementServiceDirectory serviceDirectory, IDictionary<SubscriptionName, SubscriptionDto> overrides, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var fileResources = await GetFileResources(commitId, serviceDirectory, cancellationToken);
        await ValidatePublisherChanges(fileResources, overrides, serviceUri, pipeline, cancellationToken);
    }

    private static async ValueTask<FrozenDictionary<SubscriptionName, SubscriptionDto>> GetFileResources(CommitId commitId, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await Git.GetExistingFilesInCommit(serviceDirectory.ToDirectoryInfo(), commitId)
                 .ToAsyncEnumerable()
                 .Choose(file => SubscriptionInformationFile.TryParse(file, serviceDirectory))
                 .Choose(async file => await TryGetCommitResource(commitId, serviceDirectory, file, cancellationToken))
                 .ToFrozenDictionary(cancellationToken);

    private static async ValueTask<Option<(SubscriptionName name, SubscriptionDto dto)>> TryGetCommitResource(CommitId commitId, ManagementServiceDirectory serviceDirectory, SubscriptionInformationFile file, CancellationToken cancellationToken)
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
}
