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

    public static async ValueTask Put(IEnumerable<PolicyFragmentModel> models, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await models.IterParallel(async model =>
        {
            await Put(model, serviceUri, pipeline, cancellationToken);
        }, cancellationToken);

    private static async ValueTask Put(PolicyFragmentModel model, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var uri = PolicyFragmentUri.From(model.Name, serviceUri);
        var dto = GetDto(model);

        await uri.PutDto(dto, pipeline, cancellationToken);
    }

    public static async ValueTask DeleteAll(ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await PolicyFragmentsUri.From(serviceUri).DeleteAll(pipeline, cancellationToken);

    public static async ValueTask WriteArtifacts(IEnumerable<PolicyFragmentModel> models, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await models.IterParallel(async model =>
        {
            await WriteInformationFile(model, serviceDirectory, cancellationToken);
            await WritePolicyFile(model, serviceDirectory, cancellationToken);
        }, cancellationToken);

    private static async ValueTask WriteInformationFile(PolicyFragmentModel model, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        var informationFile = PolicyFragmentInformationFile.From(model.Name, serviceDirectory);
        var dto = GetDto(model);

        await informationFile.WriteDto(dto, cancellationToken);
    }

    private static async ValueTask WritePolicyFile(PolicyFragmentModel model, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        var policyFile = PolicyFragmentPolicyFile.From(model.Name, serviceDirectory);
        await policyFile.WritePolicy(model.Content, cancellationToken);
    }

    public static async ValueTask ValidateExtractedArtifacts(Option<FrozenSet<PolicyFragmentName>> namesToExtract, ManagementServiceDirectory serviceDirectory, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var apimResources = await GetApimResources(serviceUri, pipeline, cancellationToken);
        var fileResources = await GetFileResources(serviceDirectory, cancellationToken);

        var expected = apimResources.WhereKey(name => ExtractorOptions.ShouldExtract(name, namesToExtract))
                                    .MapValue(NormalizeDto);
        var actual = fileResources.MapValue(NormalizeDto);

        actual.Should().BeEquivalentTo(expected);
    }

    private static async ValueTask<FrozenDictionary<PolicyFragmentName, PolicyFragmentDto>> GetApimResources(ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var uri = PolicyFragmentsUri.From(serviceUri);

        return await uri.List(pipeline, cancellationToken)
                        .ToFrozenDictionary(cancellationToken);
    }

    private static async ValueTask<FrozenDictionary<PolicyFragmentName, PolicyFragmentDto>> GetFileResources(ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await PolicyFragmentModule.ListInformationFiles(serviceDirectory)
                              .ToAsyncEnumerable()
                              .SelectAwait(async file => (file.Parent.Name,
                                                          await file.ReadDto(cancellationToken)))
                              .ToFrozenDictionary(cancellationToken);

    private static string NormalizeDto(PolicyFragmentDto dto) =>
        new
        {
            Description = dto.Properties.Description ?? string.Empty
        }.ToString()!;

    public static async ValueTask ValidatePublisherChanges(ManagementServiceDirectory serviceDirectory, IDictionary<PolicyFragmentName, PolicyFragmentDto> overrides, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var fileResources = await GetFileResources(serviceDirectory, cancellationToken);
        await ValidatePublisherChanges(fileResources, overrides, serviceUri, pipeline, cancellationToken);
    }

    private static async ValueTask ValidatePublisherChanges(IDictionary<PolicyFragmentName, PolicyFragmentDto> fileResources, IDictionary<PolicyFragmentName, PolicyFragmentDto> overrides, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var apimResources = await GetApimResources(serviceUri, pipeline, cancellationToken);

        var expected = PublisherOptions.Override(fileResources, overrides)
                                       .MapValue(NormalizeDto);
        var actual = apimResources.MapValue(NormalizeDto);
        actual.Should().BeEquivalentTo(expected);
    }

    public static async ValueTask ValidatePublisherCommitChanges(CommitId commitId, ManagementServiceDirectory serviceDirectory, IDictionary<PolicyFragmentName, PolicyFragmentDto> overrides, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var fileResources = await GetFileResources(commitId, serviceDirectory, cancellationToken);
        await ValidatePublisherChanges(fileResources, overrides, serviceUri, pipeline, cancellationToken);
    }

    private static async ValueTask<FrozenDictionary<PolicyFragmentName, PolicyFragmentDto>> GetFileResources(CommitId commitId, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await Git.GetExistingFilesInCommit(serviceDirectory.ToDirectoryInfo(), commitId)
                 .ToAsyncEnumerable()
                 .Choose(file => PolicyFragmentInformationFile.TryParse(file, serviceDirectory))
                 .Choose(async file => await TryGetCommitResource(commitId, serviceDirectory, file, cancellationToken))
                 .ToFrozenDictionary(cancellationToken);

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
}
