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

internal static class VersionSet
{
    public static Gen<VersionSetModel> GenerateUpdate(VersionSetModel original) =>
        from displayName in VersionSetModel.GenerateDisplayName()
        from scheme in VersioningScheme.Generate()
        from description in VersionSetModel.GenerateDescription().OptionOf()
        select original with
        {
            DisplayName = displayName,
            Scheme = scheme,
            Description = description
        };

    public static Gen<VersionSetDto> GenerateOverride(VersionSetDto original) =>
        from displayName in VersionSetModel.GenerateDisplayName()
        from header in GenerateHeaderOverride(original)
        from query in GenerateQueryOverride(original)
        from description in VersionSetModel.GenerateDescription().OptionOf()
        select new VersionSetDto
        {
            Properties = new VersionSetDto.VersionSetContract
            {
                DisplayName = displayName,
                Description = description.ValueUnsafe(),
                VersionHeaderName = header,
                VersionQueryName = query
            }
        };

    private static Gen<string?> GenerateHeaderOverride(VersionSetDto original) =>
        Gen.OneOf(Gen.Const(original.Properties.VersionHeaderName),
                  string.IsNullOrWhiteSpace(original.Properties.VersionHeaderName)
                  ? Gen.Const(() => null as string)!
                  : VersioningScheme.Header.GenerateHeaderName());

    private static Gen<string?> GenerateQueryOverride(VersionSetDto original) =>
        Gen.OneOf(Gen.Const(original.Properties.VersionQueryName),
                  string.IsNullOrWhiteSpace(original.Properties.VersionQueryName)
                  ? Gen.Const(() => null as string)!
                  : VersioningScheme.Query.GenerateQueryName());

    public static FrozenDictionary<VersionSetName, VersionSetDto> GetDtoDictionary(IEnumerable<VersionSetModel> models) =>
        models.ToFrozenDictionary(model => model.Name, GetDto);

    private static VersionSetDto GetDto(VersionSetModel model) =>
        new()
        {
            Properties = new VersionSetDto.VersionSetContract
            {
                DisplayName = model.DisplayName,
                Description = model.Description.ValueUnsafe(),
                VersionHeaderName = model.Scheme is VersioningScheme.Header header ? header.HeaderName : null,
                VersionQueryName = model.Scheme is VersioningScheme.Query query ? query.QueryName : null,
                VersioningScheme = model.Scheme switch
                {
                    VersioningScheme.Header => "Header",
                    VersioningScheme.Query => "Query",
                    VersioningScheme.Segment => "Segment",
                    _ => null
                }
            }
        };

    public static async ValueTask Put(IEnumerable<VersionSetModel> models, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await models.IterParallel(async model =>
        {
            await Put(model, serviceUri, pipeline, cancellationToken);
        }, cancellationToken);

    private static async ValueTask Put(VersionSetModel model, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var uri = VersionSetUri.From(model.Name, serviceUri);
        var dto = GetDto(model);

        await uri.PutDto(dto, pipeline, cancellationToken);
    }

    public static async ValueTask DeleteAll(ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await VersionSetsUri.From(serviceUri).DeleteAll(pipeline, cancellationToken);

    public static async ValueTask WriteArtifacts(IEnumerable<VersionSetModel> models, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await models.IterParallel(async model =>
        {
            await WriteInformationFile(model, serviceDirectory, cancellationToken);
        }, cancellationToken);

    private static async ValueTask WriteInformationFile(VersionSetModel model, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        var informationFile = VersionSetInformationFile.From(model.Name, serviceDirectory);
        var dto = GetDto(model);

        await informationFile.WriteDto(dto, cancellationToken);
    }

    public static async ValueTask ValidateExtractedArtifacts(Option<FrozenSet<VersionSetName>> namesToExtract, ManagementServiceDirectory serviceDirectory, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var apimResources = await GetApimResources(serviceUri, pipeline, cancellationToken);
        var fileResources = await GetFileResources(serviceDirectory, cancellationToken);

        var expected = apimResources.WhereKey(name => ExtractorOptions.ShouldExtract(name, namesToExtract))
                                    .MapValue(NormalizeDto);
        var actual = fileResources.MapValue(NormalizeDto);

        actual.Should().BeEquivalentTo(expected);
    }

    private static async ValueTask<FrozenDictionary<VersionSetName, VersionSetDto>> GetApimResources(ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var uri = VersionSetsUri.From(serviceUri);

        return await uri.List(pipeline, cancellationToken)
                        .ToFrozenDictionary(cancellationToken);
    }

    private static async ValueTask<FrozenDictionary<VersionSetName, VersionSetDto>> GetFileResources(ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await VersionSetModule.ListInformationFiles(serviceDirectory)
                              .ToAsyncEnumerable()
                              .SelectAwait(async file => (file.Parent.Name,
                                                          await file.ReadDto(cancellationToken)))
                              .ToFrozenDictionary(cancellationToken);

    private static string NormalizeDto(VersionSetDto dto) =>
        new
        {
            DisplayName = dto.Properties.DisplayName ?? string.Empty,
            Description = dto.Properties.Description ?? string.Empty,
            VersionHeaderName = dto.Properties.VersionHeaderName ?? string.Empty,
            VersionQueryName = dto.Properties.VersionQueryName ?? string.Empty,
            VersioningScheme = dto.Properties.VersioningScheme ?? string.Empty
        }.ToString()!;

    public static async ValueTask ValidatePublisherChanges(ManagementServiceDirectory serviceDirectory, IDictionary<VersionSetName, VersionSetDto> overrides, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var fileResources = await GetFileResources(serviceDirectory, cancellationToken);
        await ValidatePublisherChanges(fileResources, overrides, serviceUri, pipeline, cancellationToken);
    }

    private static async ValueTask ValidatePublisherChanges(IDictionary<VersionSetName, VersionSetDto> fileResources, IDictionary<VersionSetName, VersionSetDto> overrides, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var apimResources = await GetApimResources(serviceUri, pipeline, cancellationToken);

        var expected = PublisherOptions.Override(fileResources, overrides)
                                       .MapValue(NormalizeDto);
        var actual = apimResources.MapValue(NormalizeDto);
        actual.Should().BeEquivalentTo(expected);
    }

    public static async ValueTask ValidatePublisherCommitChanges(CommitId commitId, ManagementServiceDirectory serviceDirectory, IDictionary<VersionSetName, VersionSetDto> overrides, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var fileResources = await GetFileResources(commitId, serviceDirectory, cancellationToken);
        await ValidatePublisherChanges(fileResources, overrides, serviceUri, pipeline, cancellationToken);
    }

    private static async ValueTask<FrozenDictionary<VersionSetName, VersionSetDto>> GetFileResources(CommitId commitId, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await Git.GetExistingFilesInCommit(serviceDirectory.ToDirectoryInfo(), commitId)
                 .ToAsyncEnumerable()
                 .Choose(file => VersionSetInformationFile.TryParse(file, serviceDirectory))
                 .Choose(async file => await TryGetCommitResource(commitId, serviceDirectory, file, cancellationToken))
                 .ToFrozenDictionary(cancellationToken);

    private static async ValueTask<Option<(VersionSetName name, VersionSetDto dto)>> TryGetCommitResource(CommitId commitId, ManagementServiceDirectory serviceDirectory, VersionSetInformationFile file, CancellationToken cancellationToken)
    {
        var name = file.Parent.Name;
        var contentsOption = Git.TryGetFileContentsInCommit(serviceDirectory.ToDirectoryInfo(), file.ToFileInfo(), commitId);

        return await contentsOption.MapTask(async contents =>
        {
            using (contents)
            {
                var data = await BinaryData.FromStreamAsync(contents, cancellationToken);
                var dto = data.ToObjectFromJson<VersionSetDto>();
                return (name, dto);
            }
        });
    }
}
