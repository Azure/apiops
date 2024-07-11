using Azure.Core.Pipeline;
using common;
using LanguageExt;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

public delegate ValueTask ExtractGroups(CancellationToken cancellationToken);
public delegate IAsyncEnumerable<(GroupName Name, GroupDto Dto)> ListGroups(CancellationToken cancellationToken);
public delegate bool ShouldExtractGroup(GroupName name);
public delegate ValueTask WriteGroupArtifacts(GroupName name, GroupDto dto, CancellationToken cancellationToken);
public delegate ValueTask WriteGroupInformationFile(GroupName name, GroupDto dto, CancellationToken cancellationToken);

internal static class GroupModule
{
    public static void ConfigureExtractGroups(IHostApplicationBuilder builder)
    {
        ConfigureListGroups(builder);
        ConfigureShouldExtractGroup(builder);
        ConfigureWriteGroupArtifacts(builder);

        builder.Services.TryAddSingleton(GetExtractGroups);
    }

    private static ExtractGroups GetExtractGroups(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListGroups>();
        var shouldExtract = provider.GetRequiredService<ShouldExtractGroup>();
        var writeArtifacts = provider.GetRequiredService<WriteGroupArtifacts>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractGroups));

            logger.LogInformation("Extracting groups...");

            await list(cancellationToken)
                    .Where(group => shouldExtract(group.Name))
                    .IterParallel(async group => await writeArtifacts(group.Name, group.Dto, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureListGroups(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListGroups);
    }

    private static ListGroups GetListGroups(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return cancellationToken =>
            GroupsUri.From(serviceUri)
                     .List(pipeline, cancellationToken);
    }

    public static void ConfigureShouldExtractGroup(IHostApplicationBuilder builder)
    {
        ShouldExtractModule.ConfigureShouldExtractFactory(builder);

        builder.Services.TryAddSingleton(GetShouldExtractGroup);
    }

    private static ShouldExtractGroup GetShouldExtractGroup(IServiceProvider provider)
    {
        var shouldExtractFactory = provider.GetRequiredService<ShouldExtractFactory>();

        var shouldExtract = shouldExtractFactory.Create<GroupName>();

        return name => shouldExtract(name);
    }

    private static void ConfigureWriteGroupArtifacts(IHostApplicationBuilder builder)
    {
        ConfigureWriteGroupInformationFile(builder);

        builder.Services.TryAddSingleton(GetWriteGroupArtifacts);
    }

    private static WriteGroupArtifacts GetWriteGroupArtifacts(IServiceProvider provider)
    {
        var writeInformationFile = provider.GetRequiredService<WriteGroupInformationFile>();

        return async (name, dto, cancellationToken) =>
        {
            await writeInformationFile(name, dto, cancellationToken);
        };
    }

    private static void ConfigureWriteGroupInformationFile(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetWriteGroupInformationFile);
    }

    private static WriteGroupInformationFile GetWriteGroupInformationFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, cancellationToken) =>
        {
            var informationFile = GroupInformationFile.From(name, serviceDirectory);

            logger.LogInformation("Writing group information file {GroupInformationFile}...", informationFile);
            await informationFile.WriteDto(dto, cancellationToken);
        };
    }
}