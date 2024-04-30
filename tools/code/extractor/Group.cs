using Azure.Core.Pipeline;
using common;
using LanguageExt;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal delegate ValueTask ExtractGroups(CancellationToken cancellationToken);

file delegate IAsyncEnumerable<(GroupName Name, GroupDto Dto)> ListGroups(CancellationToken cancellationToken);

file delegate bool ShouldExtractGroup(GroupName name);

file delegate ValueTask WriteGroupArtifacts(GroupName name, GroupDto dto, CancellationToken cancellationToken);

file delegate ValueTask WriteGroupInformationFile(GroupName name, GroupDto dto, CancellationToken cancellationToken);

file sealed class ExtractGroupsHandler(ListGroups list, ShouldExtractGroup shouldExtract, WriteGroupArtifacts writeArtifacts)
{
    public async ValueTask Handle(CancellationToken cancellationToken) =>
        await list(cancellationToken)
                .Where(group => shouldExtract(group.Name))
                .IterParallel(async group => await writeArtifacts(group.Name, group.Dto, cancellationToken),
                              cancellationToken);
}

file sealed class ListGroupsHandler(ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    public IAsyncEnumerable<(GroupName, GroupDto)> Handle(CancellationToken cancellationToken) =>
        GroupsUri.From(serviceUri).List(pipeline, cancellationToken);
}

file sealed class ShouldExtractGroupHandler(ShouldExtractFactory shouldExtractFactory)
{
    public bool Handle(GroupName name)
    {
        var shouldExtract = shouldExtractFactory.Create<GroupName>();
        return shouldExtract(name);
    }
}

file sealed class WriteGroupArtifactsHandler(WriteGroupInformationFile writeInformationFile)
{
    public async ValueTask Handle(GroupName name, GroupDto dto, CancellationToken cancellationToken)
    {
        await writeInformationFile(name, dto, cancellationToken);
    }
}

file sealed class WriteGroupInformationFileHandler(ILoggerFactory loggerFactory, ManagementServiceDirectory serviceDirectory)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(GroupName name, GroupDto dto, CancellationToken cancellationToken)
    {
        var informationFile = GroupInformationFile.From(name, serviceDirectory);

        logger.LogInformation("Writing group information file {InformationFile}", informationFile);
        await informationFile.WriteDto(dto, cancellationToken);
    }
}

internal static class GroupServices
{
    public static void ConfigureExtractGroups(IServiceCollection services)
    {
        ConfigureListGroups(services);
        ConfigureShouldExtractGroup(services);
        ConfigureWriteGroupArtifacts(services);

        services.TryAddSingleton<ExtractGroupsHandler>();
        services.TryAddSingleton<ExtractGroups>(provider => provider.GetRequiredService<ExtractGroupsHandler>().Handle);
    }

    private static void ConfigureListGroups(IServiceCollection services)
    {
        services.TryAddSingleton<ListGroupsHandler>();
        services.TryAddSingleton<ListGroups>(provider => provider.GetRequiredService<ListGroupsHandler>().Handle);
    }

    private static void ConfigureShouldExtractGroup(IServiceCollection services)
    {
        services.TryAddSingleton<ShouldExtractGroupHandler>();
        services.TryAddSingleton<ShouldExtractGroup>(provider => provider.GetRequiredService<ShouldExtractGroupHandler>().Handle);
    }

    private static void ConfigureWriteGroupArtifacts(IServiceCollection services)
    {
        ConfigureWriteGroupInformationFile(services);

        services.TryAddSingleton<WriteGroupArtifactsHandler>();
        services.TryAddSingleton<WriteGroupArtifacts>(provider => provider.GetRequiredService<WriteGroupArtifactsHandler>().Handle);
    }

    private static void ConfigureWriteGroupInformationFile(IServiceCollection services)
    {
        services.TryAddSingleton<WriteGroupInformationFileHandler>();
        services.TryAddSingleton<WriteGroupInformationFile>(provider => provider.GetRequiredService<WriteGroupInformationFileHandler>().Handle);
    }
}

file static class Common
{
    public static ILogger GetLogger(ILoggerFactory loggerFactory) =>
        loggerFactory.CreateLogger("GroupExtractor");
}