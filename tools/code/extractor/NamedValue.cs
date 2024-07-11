﻿using Azure.Core.Pipeline;
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

public delegate ValueTask ExtractNamedValues(CancellationToken cancellationToken);
public delegate IAsyncEnumerable<(NamedValueName Name, NamedValueDto Dto)> ListNamedValues(CancellationToken cancellationToken);
public delegate bool ShouldExtractNamedValue(NamedValueName name);
public delegate ValueTask WriteNamedValueArtifacts(NamedValueName name, NamedValueDto dto, CancellationToken cancellationToken);
public delegate ValueTask WriteNamedValueInformationFile(NamedValueName name, NamedValueDto dto, CancellationToken cancellationToken);

internal static class NamedValueModule
{
    public static void ConfigureExtractNamedValues(IHostApplicationBuilder builder)
    {
        ConfigureListNamedValues(builder);
        ConfigureShouldExtractNamedValue(builder);
        ConfigureWriteNamedValueArtifacts(builder);

        builder.Services.TryAddSingleton(GetExtractNamedValues);
    }

    private static ExtractNamedValues GetExtractNamedValues(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListNamedValues>();
        var shouldExtract = provider.GetRequiredService<ShouldExtractNamedValue>();
        var writeArtifacts = provider.GetRequiredService<WriteNamedValueArtifacts>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractNamedValues));

            logger.LogInformation("Extracting named values...");

            await list(cancellationToken)
                    .Where(namedvalue => shouldExtract(namedvalue.Name))
                    .IterParallel(async namedvalue => await writeArtifacts(namedvalue.Name, namedvalue.Dto, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureListNamedValues(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListNamedValues);
    }

    private static ListNamedValues GetListNamedValues(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return cancellationToken =>
            NamedValuesUri.From(serviceUri)
                          .List(pipeline, cancellationToken);
    }

    private static void ConfigureShouldExtractNamedValue(IHostApplicationBuilder builder)
    {
        ShouldExtractModule.ConfigureShouldExtractFactory(builder);

        builder.Services.TryAddSingleton(GetShouldExtractNamedValue);
    }

    private static ShouldExtractNamedValue GetShouldExtractNamedValue(IServiceProvider provider)
    {
        var shouldExtractFactory = provider.GetRequiredService<ShouldExtractFactory>();

        var shouldExtract = shouldExtractFactory.Create<NamedValueName>();

        return name => shouldExtract(name);
    }

    private static void ConfigureWriteNamedValueArtifacts(IHostApplicationBuilder builder)
    {
        ConfigureWriteNamedValueInformationFile(builder);

        builder.Services.TryAddSingleton(GetWriteNamedValueArtifacts);
    }

    private static WriteNamedValueArtifacts GetWriteNamedValueArtifacts(IServiceProvider provider)
    {
        var writeInformationFile = provider.GetRequiredService<WriteNamedValueInformationFile>();

        return async (name, dto, cancellationToken) =>
            await writeInformationFile(name, dto, cancellationToken);
    }

    private static void ConfigureWriteNamedValueInformationFile(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetWriteNamedValueInformationFile);
    }

    private static WriteNamedValueInformationFile GetWriteNamedValueInformationFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, cancellationToken) =>
        {
            var informationFile = NamedValueInformationFile.From(name, serviceDirectory);

            logger.LogInformation("Writing named value information file {NamedValueInformationFile}...", informationFile);
            await informationFile.WriteDto(dto, cancellationToken);
        };
    }
}