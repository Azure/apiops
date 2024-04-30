using Azure.Core.Pipeline;
using common;
using common.tests;
using CsCheck;
using FluentAssertions;
using LanguageExt;
using publisher;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace integration.tests;

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

    public static async ValueTask Put(IEnumerable<ServicePolicyModel> models, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await models.IterParallel(async model =>
        {
            await Put(model, serviceUri, pipeline, cancellationToken);
        }, cancellationToken);

    private static async ValueTask Put(ServicePolicyModel model, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var uri = ServicePolicyUri.From(model.Name, serviceUri);
        var dto = GetDto(model);

        await uri.PutDto(dto, pipeline, cancellationToken);
    }

    public static async ValueTask DeleteAll(ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await ServicePoliciesUri.From(serviceUri).DeleteAll(pipeline, cancellationToken);

    public static async ValueTask WriteArtifacts(IEnumerable<ServicePolicyModel> models, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await models.IterParallel(async model =>
        {
            await WritePolicyFile(model, serviceDirectory, cancellationToken);
        }, cancellationToken);

    private static async ValueTask WritePolicyFile(ServicePolicyModel model, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        var policyFile = ServicePolicyFile.From(model.Name, serviceDirectory);
        await policyFile.WritePolicy(model.Content, cancellationToken);
    }

    public static async ValueTask ValidateExtractedArtifacts(ManagementServiceDirectory serviceDirectory, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var apimResources = await GetApimResources(serviceUri, pipeline, cancellationToken);
        var fileResources = await GetFileResources(serviceDirectory, cancellationToken);

        var expected = apimResources.MapValue(NormalizeDto);
        var actual = fileResources.MapValue(NormalizeDto);

        actual.Should().BeEquivalentTo(expected);
    }

    private static async ValueTask<FrozenDictionary<ServicePolicyName, ServicePolicyDto>> GetApimResources(ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var uri = ServicePoliciesUri.From(serviceUri);

        return await uri.List(pipeline, cancellationToken)
                        .ToFrozenDictionary(cancellationToken);
    }

    private static async ValueTask<FrozenDictionary<ServicePolicyName, ServicePolicyDto>> GetFileResources(ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await ServicePolicyModule.ListPolicyFiles(serviceDirectory)
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

    private static string NormalizeDto(ServicePolicyDto dto) =>
        new
        {
            Value = new string((dto.Properties.Value ?? string.Empty)
                                .ReplaceLineEndings(string.Empty)
                                .Where(c => char.IsWhiteSpace(c) is false)
                                .ToArray())
        }.ToString()!;

    public static async ValueTask ValidatePublisherChanges(ManagementServiceDirectory serviceDirectory, IDictionary<ServicePolicyName, ServicePolicyDto> overrides, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var fileResources = await GetFileResources(serviceDirectory, cancellationToken);
        await ValidatePublisherChanges(fileResources, overrides, serviceUri, pipeline, cancellationToken);
    }

    private static async ValueTask ValidatePublisherChanges(IDictionary<ServicePolicyName, ServicePolicyDto> fileResources, IDictionary<ServicePolicyName, ServicePolicyDto> overrides, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var apimResources = await GetApimResources(serviceUri, pipeline, cancellationToken);

        var expected = PublisherOptions.Override(fileResources, overrides)
                                       .MapValue(NormalizeDto);
        var actual = apimResources.MapValue(NormalizeDto);
        actual.Should().BeEquivalentTo(expected);
    }

    public static async ValueTask ValidatePublisherCommitChanges(CommitId commitId, ManagementServiceDirectory serviceDirectory, IDictionary<ServicePolicyName, ServicePolicyDto> overrides, ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var fileResources = await GetFileResources(commitId, serviceDirectory, cancellationToken);
        await ValidatePublisherChanges(fileResources, overrides, serviceUri, pipeline, cancellationToken);
    }

    private static async ValueTask<FrozenDictionary<ServicePolicyName, ServicePolicyDto>> GetFileResources(CommitId commitId, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
        await Git.GetExistingFilesInCommit(serviceDirectory.ToDirectoryInfo(), commitId)
                 .ToAsyncEnumerable()
                 .Choose(file => ServicePolicyFile.TryParse(file, serviceDirectory))
                 .Choose(async file => await TryGetCommitResource(commitId, serviceDirectory, file, cancellationToken))
                 .ToFrozenDictionary(cancellationToken);

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
}
