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

internal static class Backend
{
    public static Gen<BackendModel> GenerateUpdate(BackendModel original) =>
        from url in Generator.AbsoluteUri
        from description in BackendModel.GenerateDescription().OptionOf()
        select original with
        {
            Url = url,
            Description = description
        };

    public static Gen<BackendDto> GenerateOverride(BackendDto original) =>
        from url in Generator.AbsoluteUri
        from description in BackendModel.GenerateDescription().OptionOf()
        select new BackendDto
        {
            Properties = new BackendDto.BackendContract
            {
                Url = url.ToString(),
                Description = description.ValueUnsafe()
            }
        };

    public static FrozenDictionary<BackendName, BackendDto> GetDtoDictionary(IEnumerable<BackendModel> models) =>
        models.ToFrozenDictionary(model => model.Name, GetDto);

    private static BackendDto GetDto(BackendModel model) =>
        new()
        {
            Properties = new BackendDto.BackendContract
            {
                Url = model.Url.ToString(),
                Description = model.Description.ValueUnsafe(),
                Protocol = model.Protocol
            }
        };

    public static async ValueTask Put(IEnumerable<BackendModel> models, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await models.IterParallel(async model =>
        {
            await Put(model, serviceUri, pipeline, cancellationToken);
        }, cancellationToken);

    private static async ValueTask Put(BackendModel model, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var uri = BackendUri.From(model.Name, serviceUri);
        var dto = GetDto(model);

        await uri.PutDto(dto, pipeline, cancellationToken);
    }

    public static async ValueTask DeleteAll(ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await BackendsUri.From(serviceUri).DeleteAll(pipeline, cancellationToken);

    public static async ValueTask WriteArtifacts(IEnumerable<BackendModel> models, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await models.IterParallel(async model =>
        {
            await WriteInformationFile(model, serviceDirectory, cancellationToken);
        }, cancellationToken);

    private static async ValueTask WriteInformationFile(BackendModel model, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        var informationFile = BackendInformationFile.From(model.Name, serviceDirectory);
        var dto = GetDto(model);

        await informationFile.WriteDto(dto, cancellationToken);
    }

    public static async ValueTask ValidateExtractedArtifacts(Option<FrozenSet<BackendName>> namesToExtract, ManagementServiceDirectory serviceDirectory, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var apimResources = await GetApimResources(serviceUri, pipeline, cancellationToken);
        var fileResources = await GetFileResources(serviceDirectory, cancellationToken);

        var expected = apimResources.WhereKey(name => ExtractorOptions.ShouldExtract(name, namesToExtract))
                                    .MapValue(NormalizeDto);
        var actual = fileResources.MapValue(NormalizeDto);

        actual.Should().BeEquivalentTo(expected);
    }

    private static async ValueTask<FrozenDictionary<BackendName, BackendDto>> GetApimResources(ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var uri = BackendsUri.From(serviceUri);

        return await uri.List(pipeline, cancellationToken)
                        .ToFrozenDictionary(cancellationToken);
    }

    private static async ValueTask<FrozenDictionary<BackendName, BackendDto>> GetFileResources(ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await BackendModule.ListInformationFiles(serviceDirectory)
                              .ToAsyncEnumerable()
                              .SelectAwait(async file => (file.Parent.Name,
                                                          await file.ReadDto(cancellationToken)))
                              .ToFrozenDictionary(cancellationToken);

    private static string NormalizeDto(BackendDto dto) =>
        new
        {
            Url = dto.Properties.Url ?? string.Empty,
            Description = dto.Properties.Description ?? string.Empty,
            Protocol = dto.Properties.Protocol ?? string.Empty
        }.ToString()!;

    public static async ValueTask ValidatePublisherChanges(ManagementServiceDirectory serviceDirectory, IDictionary<BackendName, BackendDto> overrides, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var fileResources = await GetFileResources(serviceDirectory, cancellationToken);
        await ValidatePublisherChanges(fileResources, overrides, serviceUri, pipeline, cancellationToken);
    }

    private static async ValueTask ValidatePublisherChanges(IDictionary<BackendName, BackendDto> fileResources, IDictionary<BackendName, BackendDto> overrides, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var apimResources = await GetApimResources(serviceUri, pipeline, cancellationToken);

        var expected = PublisherOptions.Override(fileResources, overrides)
                                       .MapValue(NormalizeDto);
        var actual = apimResources.MapValue(NormalizeDto);
        actual.Should().BeEquivalentTo(expected);
    }

    public static async ValueTask ValidatePublisherCommitChanges(CommitId commitId, ManagementServiceDirectory serviceDirectory, IDictionary<BackendName, BackendDto> overrides, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var fileResources = await GetFileResources(commitId, serviceDirectory, cancellationToken);
        await ValidatePublisherChanges(fileResources, overrides, serviceUri, pipeline, cancellationToken);
    }

    private static async ValueTask<FrozenDictionary<BackendName, BackendDto>> GetFileResources(CommitId commitId, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await Git.GetExistingFilesInCommit(serviceDirectory.ToDirectoryInfo(), commitId)
                 .ToAsyncEnumerable()
                 .Choose(file => BackendInformationFile.TryParse(file, serviceDirectory))
                 .Choose(async file => await TryGetCommitResource(commitId, serviceDirectory, file, cancellationToken))
                 .ToFrozenDictionary(cancellationToken);

    private static async ValueTask<Option<(BackendName name, BackendDto dto)>> TryGetCommitResource(CommitId commitId, ManagementServiceDirectory serviceDirectory, BackendInformationFile file, CancellationToken cancellationToken)
    {
        var name = file.Parent.Name;
        var contentsOption = Git.TryGetFileContentsInCommit(serviceDirectory.ToDirectoryInfo(), file.ToFileInfo(), commitId);

        return await contentsOption.MapTask(async contents =>
        {
            using (contents)
            {
                var data = await BinaryData.FromStreamAsync(contents, cancellationToken);
                var dto = data.ToObjectFromJson<BackendDto>();
                return (name, dto);
            }
        });
    }
}
