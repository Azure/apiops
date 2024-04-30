using Azure.Core.Pipeline;
using common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal delegate ValueTask ExtractProductGroups(ProductName productName, CancellationToken cancellationToken);

file delegate IAsyncEnumerable<(GroupName Name, ProductGroupDto Dto)> ListProductGroups(ProductName productName, CancellationToken cancellationToken);

file delegate ValueTask WriteProductGroupArtifacts(GroupName name, ProductGroupDto dto, ProductName productName, CancellationToken cancellationToken);

file delegate ValueTask WriteProductGroupInformationFile(GroupName name, ProductGroupDto dto, ProductName productName, CancellationToken cancellationToken);

file sealed class ExtractProductGroupsHandler(ListProductGroups list, WriteProductGroupArtifacts writeArtifacts)
{
    public async ValueTask Handle(ProductName productName, CancellationToken cancellationToken) =>
        await list(productName, cancellationToken)
                .IterParallel(async productgroup => await writeArtifacts(productgroup.Name, productgroup.Dto, productName, cancellationToken),
                              cancellationToken);
}

file sealed class ListProductGroupsHandler(ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    public IAsyncEnumerable<(GroupName, ProductGroupDto)> Handle(ProductName productName, CancellationToken cancellationToken) =>
        ProductGroupsUri.From(productName, serviceUri).List(pipeline, cancellationToken);
}

file sealed class WriteProductGroupArtifactsHandler(WriteProductGroupInformationFile writeGroupFile)
{
    public async ValueTask Handle(GroupName name, ProductGroupDto dto, ProductName productName, CancellationToken cancellationToken)
    {
        await writeGroupFile(name, dto, productName, cancellationToken);
    }
}

file sealed class WriteProductGroupInformationFileHandler(ILoggerFactory loggerFactory, ManagementServiceDirectory serviceDirectory)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(GroupName name, ProductGroupDto dto, ProductName productName, CancellationToken cancellationToken)
    {
        var informationFile = ProductGroupInformationFile.From(name, productName, serviceDirectory);

        logger.LogInformation("Writing product group information file {ProductGroupInformationFile}...", informationFile);
        await informationFile.WriteDto(dto, cancellationToken);
    }
}

internal static class ProductGroupServices
{
    public static void ConfigureExtractProductGroups(IServiceCollection services)
    {
        ConfigureListProductGroups(services);
        ConfigureWriteProductGroupArtifacts(services);

        services.TryAddSingleton<ExtractProductGroupsHandler>();
        services.TryAddSingleton<ExtractProductGroups>(provider => provider.GetRequiredService<ExtractProductGroupsHandler>().Handle);
    }

    private static void ConfigureListProductGroups(IServiceCollection services)
    {
        services.TryAddSingleton<ListProductGroupsHandler>();
        services.TryAddSingleton<ListProductGroups>(provider => provider.GetRequiredService<ListProductGroupsHandler>().Handle);
    }

    private static void ConfigureWriteProductGroupArtifacts(IServiceCollection services)
    {
        ConfigureWriteProductGroupInformationFile(services);

        services.TryAddSingleton<WriteProductGroupArtifactsHandler>();
        services.TryAddSingleton<WriteProductGroupArtifacts>(provider => provider.GetRequiredService<WriteProductGroupArtifactsHandler>().Handle);
    }

    private static void ConfigureWriteProductGroupInformationFile(IServiceCollection services)
    {
        services.TryAddSingleton<WriteProductGroupInformationFileHandler>();
        services.TryAddSingleton<WriteProductGroupInformationFile>(provider => provider.GetRequiredService<WriteProductGroupInformationFileHandler>().Handle);
    }
}

file static class Common
{
    public static ILogger GetLogger(ILoggerFactory loggerFactory) =>
        loggerFactory.CreateLogger("ProductGroupExtractor");
}