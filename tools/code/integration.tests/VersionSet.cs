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

internal delegate ValueTask DeleteAllVersionSets(ManagementServiceName serviceName, CancellationToken cancellationToken);

internal delegate ValueTask PutVersionSetModels(IEnumerable<VersionSetModel> models, ManagementServiceName serviceName, CancellationToken cancellationToken);

internal delegate ValueTask ValidateExtractedVersionSets(Option<FrozenSet<VersionSetName>> namesFilterOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

file delegate ValueTask<FrozenDictionary<VersionSetName, VersionSetDto>> GetApimVersionSets(ManagementServiceName serviceName, CancellationToken cancellationToken);

file delegate ValueTask<FrozenDictionary<VersionSetName, VersionSetDto>> GetFileVersionSets(ManagementServiceDirectory serviceDirectory, Option<CommitId> commitIdOption, CancellationToken cancellationToken);

internal delegate ValueTask WriteVersionSetModels(IEnumerable<VersionSetModel> models, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

internal delegate ValueTask ValidatePublishedVersionSets(IDictionary<VersionSetName, VersionSetDto> overrides, Option<CommitId> commitIdOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

file sealed class DeleteAllVersionSetsHandler(ILogger<DeleteAllVersionSets> logger, GetManagementServiceUri getServiceUri, HttpPipeline pipeline, ActivitySource activitySource)
{
    public async ValueTask Handle(ManagementServiceName serviceName, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(DeleteAllVersionSets));

        logger.LogInformation("Deleting all version sets in {ServiceName}...", serviceName);
        var serviceUri = getServiceUri(serviceName);
        await VersionSetsUri.From(serviceUri).DeleteAll(pipeline, cancellationToken);
    }
}

file sealed class PutVersionSetModelsHandler(ILogger<PutVersionSetModels> logger, GetManagementServiceUri getServiceUri, HttpPipeline pipeline, ActivitySource activitySource)
{
    public async ValueTask Handle(IEnumerable<VersionSetModel> models, ManagementServiceName serviceName, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(PutVersionSetModels));

        logger.LogInformation("Putting version set models in {ServiceName}...", serviceName);
        await models.IterParallel(async model =>
        {
            await Put(model, serviceName, cancellationToken);
        }, cancellationToken);
    }

    private async ValueTask Put(VersionSetModel model, ManagementServiceName serviceName, CancellationToken cancellationToken)
    {
        var serviceUri = getServiceUri(serviceName);
        var uri = VersionSetUri.From(model.Name, serviceUri);
        var dto = GetDto(model);

        await uri.PutDto(dto, pipeline, cancellationToken);
    }

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
}

file sealed class ValidateExtractedVersionSetsHandler(ILogger<ValidateExtractedVersionSets> logger, GetApimVersionSets getApimResources, GetFileVersionSets getFileResources, ActivitySource activitySource)
{
    public async ValueTask Handle(Option<FrozenSet<VersionSetName>> namesFilterOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(ValidateExtractedVersionSets));

        logger.LogInformation("Validating extracted version sets in {ServiceName}...", serviceName);
        var apimResources = await getApimResources(serviceName, cancellationToken);
        var fileResources = await getFileResources(serviceDirectory, Prelude.None, cancellationToken);

        var expected = apimResources.WhereKey(name => ExtractorOptions.ShouldExtract(name, namesFilterOption))
                                    .MapValue(NormalizeDto);
        var actual = fileResources.MapValue(NormalizeDto);

        actual.Should().BeEquivalentTo(expected);
    }

    private static string NormalizeDto(VersionSetDto dto) =>
        new
        {
            DisplayName = dto.Properties.DisplayName ?? string.Empty,
            Description = dto.Properties.Description ?? string.Empty,
            VersionHeaderName = dto.Properties.VersionHeaderName ?? string.Empty,
            VersionQueryName = dto.Properties.VersionQueryName ?? string.Empty,
            VersioningScheme = dto.Properties.VersioningScheme ?? string.Empty
        }.ToString()!;
}

file sealed class GetApimVersionSetsHandler(ILogger<GetApimVersionSets> logger, GetManagementServiceUri getServiceUri, HttpPipeline pipeline, ActivitySource activitySource)
{
    public async ValueTask<FrozenDictionary<VersionSetName, VersionSetDto>> Handle(ManagementServiceName serviceName, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(GetApimVersionSets));

        logger.LogInformation("Getting version sets from {ServiceName}...", serviceName);

        var serviceUri = getServiceUri(serviceName);
        var uri = VersionSetsUri.From(serviceUri);

        return await uri.List(pipeline, cancellationToken)
                        .ToFrozenDictionary(cancellationToken);
    }
}

file sealed class GetFileVersionSetsHandler(ILogger<GetFileVersionSets> logger, ActivitySource activitySource)
{
    public async ValueTask<FrozenDictionary<VersionSetName, VersionSetDto>> Handle(ManagementServiceDirectory serviceDirectory, Option<CommitId> commitIdOption, CancellationToken cancellationToken) =>
        await commitIdOption.Map(commitId => GetWithCommit(serviceDirectory, commitId, cancellationToken))
                           .IfNone(() => GetWithoutCommit(serviceDirectory, cancellationToken));

    private async ValueTask<FrozenDictionary<VersionSetName, VersionSetDto>> GetWithCommit(ManagementServiceDirectory serviceDirectory, CommitId commitId, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(GetFileVersionSets));

        logger.LogInformation("Getting version sets from {ServiceDirectory} as of commit {CommitId}...", serviceDirectory, commitId);

        return await Git.GetExistingFilesInCommit(serviceDirectory.ToDirectoryInfo(), commitId)
                        .ToAsyncEnumerable()
                        .Choose(file => VersionSetInformationFile.TryParse(file, serviceDirectory))
                        .Choose(async file => await TryGetCommitResource(commitId, serviceDirectory, file, cancellationToken))
                        .ToFrozenDictionary(cancellationToken);
    }

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

    private async ValueTask<FrozenDictionary<VersionSetName, VersionSetDto>> GetWithoutCommit(ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(GetFileVersionSets));

        logger.LogInformation("Getting version sets from {ServiceDirectory}...", serviceDirectory);

        return await VersionSetModule.ListInformationFiles(serviceDirectory)
                                     .ToAsyncEnumerable()
                                     .SelectAwait(async file => (file.Parent.Name,
                                                                 await file.ReadDto(cancellationToken)))
                                     .ToFrozenDictionary(cancellationToken);
    }
}

file sealed class WriteVersionSetModelsHandler(ILogger<WriteVersionSetModels> logger, ActivitySource activitySource)
{
    public async ValueTask Handle(IEnumerable<VersionSetModel> models, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(WriteVersionSetModels));

        logger.LogInformation("Writing version set models to {ServiceDirectory}...", serviceDirectory);
        await models.IterParallel(async model =>
        {
            await WriteInformationFile(model, serviceDirectory, cancellationToken);
        }, cancellationToken);
    }

    private static async ValueTask WriteInformationFile(VersionSetModel model, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        var informationFile = VersionSetInformationFile.From(model.Name, serviceDirectory);
        var dto = GetDto(model);

        await informationFile.WriteDto(dto, cancellationToken);
    }

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
}

file sealed class ValidatePublishedVersionSetsHandler(ILogger<ValidatePublishedVersionSets> logger, GetFileVersionSets getFileResources, GetApimVersionSets getApimResources, ActivitySource activitySource)
{
    public async ValueTask Handle(IDictionary<VersionSetName, VersionSetDto> overrides, Option<CommitId> commitIdOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(ValidatePublishedVersionSets));

        logger.LogInformation("Validating published version sets in {ServiceDirectory}...", serviceDirectory);

        var apimResources = await getApimResources(serviceName, cancellationToken);
        var fileResources = await getFileResources(serviceDirectory, commitIdOption, cancellationToken);

        var expected = PublisherOptions.Override(fileResources, overrides)
                                       .MapValue(NormalizeDto);
        var actual = apimResources.MapValue(NormalizeDto);

        actual.Should().BeEquivalentTo(expected);
    }

    private static string NormalizeDto(VersionSetDto dto) =>
        new
        {
            DisplayName = dto.Properties.DisplayName ?? string.Empty,
            Description = dto.Properties.Description ?? string.Empty,
            VersionHeaderName = dto.Properties.VersionHeaderName ?? string.Empty,
            VersionQueryName = dto.Properties.VersionQueryName ?? string.Empty,
            VersioningScheme = dto.Properties.VersioningScheme ?? string.Empty
        }.ToString()!;
}

internal static class VersionSetServices
{
    public static void ConfigureDeleteAllVersionSets(IServiceCollection services)
    {
        ManagementServices.ConfigureGetManagementServiceUri(services);

        services.TryAddSingleton<DeleteAllVersionSetsHandler>();
        services.TryAddSingleton<DeleteAllVersionSets>(provider => provider.GetRequiredService<DeleteAllVersionSetsHandler>().Handle);
    }

    public static void ConfigurePutVersionSetModels(IServiceCollection services)
    {
        ManagementServices.ConfigureGetManagementServiceUri(services);

        services.TryAddSingleton<PutVersionSetModelsHandler>();
        services.TryAddSingleton<PutVersionSetModels>(provider => provider.GetRequiredService<PutVersionSetModelsHandler>().Handle);
    }

    public static void ConfigureValidateExtractedVersionSets(IServiceCollection services)
    {
        ConfigureGetApimVersionSets(services);
        ConfigureGetFileVersionSets(services);

        services.TryAddSingleton<ValidateExtractedVersionSetsHandler>();
        services.TryAddSingleton<ValidateExtractedVersionSets>(provider => provider.GetRequiredService<ValidateExtractedVersionSetsHandler>().Handle);
    }

    private static void ConfigureGetApimVersionSets(IServiceCollection services)
    {
        ManagementServices.ConfigureGetManagementServiceUri(services);

        services.TryAddSingleton<GetApimVersionSetsHandler>();
        services.TryAddSingleton<GetApimVersionSets>(provider => provider.GetRequiredService<GetApimVersionSetsHandler>().Handle);
    }

    private static void ConfigureGetFileVersionSets(IServiceCollection services)
    {
        services.TryAddSingleton<GetFileVersionSetsHandler>();
        services.TryAddSingleton<GetFileVersionSets>(provider => provider.GetRequiredService<GetFileVersionSetsHandler>().Handle);
    }

    public static void ConfigureWriteVersionSetModels(IServiceCollection services)
    {
        services.TryAddSingleton<WriteVersionSetModelsHandler>();
        services.TryAddSingleton<WriteVersionSetModels>(provider => provider.GetRequiredService<WriteVersionSetModelsHandler>().Handle);
    }

    public static void ConfigureValidatePublishedVersionSets(IServiceCollection services)
    {
        ConfigureGetFileVersionSets(services);
        ConfigureGetApimVersionSets(services);

        services.TryAddSingleton<ValidatePublishedVersionSetsHandler>();
        services.TryAddSingleton<ValidatePublishedVersionSets>(provider => provider.GetRequiredService<ValidatePublishedVersionSetsHandler>().Handle);
    }
}

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
}
