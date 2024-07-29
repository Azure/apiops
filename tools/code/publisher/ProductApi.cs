using Azure.Core.Pipeline;
using common;
using LanguageExt;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace publisher;

public delegate ValueTask PutProductApis(CancellationToken cancellationToken);
public delegate Option<(ApiName Name, ProductName ProductName)> TryParseProductApiName(FileInfo file);
public delegate bool IsProductApiNameInSourceControl(ApiName name, ProductName productName);
public delegate ValueTask PutProductApi(ApiName name, ProductName productName, CancellationToken cancellationToken);
public delegate ValueTask<Option<ProductApiDto>> FindProductApiDto(ApiName name, ProductName productName, CancellationToken cancellationToken);
public delegate ValueTask PutProductApiInApim(ApiName name, ProductApiDto dto, ProductName productName, CancellationToken cancellationToken);
public delegate ValueTask DeleteProductApis(CancellationToken cancellationToken);
public delegate ValueTask DeleteProductApi(ApiName name, ProductName productName, CancellationToken cancellationToken);
public delegate ValueTask DeleteProductApiFromApim(ApiName name, ProductName productName, CancellationToken cancellationToken);

internal static class ProductApiModule
{
    public static void ConfigurePutProductApis(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryProductParseApiName(builder);
        ConfigureIsProductApiNameInSourceControl(builder);
        ConfigurePutProductApi(builder);

        builder.Services.TryAddSingleton(GetPutProductApis);
    }

    private static PutProductApis GetPutProductApis(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseProductApiName>();
        var isNameInSourceControl = provider.GetRequiredService<IsProductApiNameInSourceControl>();
        var put = provider.GetRequiredService<PutProductApi>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(PutProductApis));

            logger.LogInformation("Putting product apis...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(api => isNameInSourceControl(api.Name, api.ProductName))
                    .Distinct()
                    .IterParallel(put.Invoke, cancellationToken);
        };
    }

    private static void ConfigureTryProductParseApiName(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetTryParseProductApiName);
    }

    private static TryParseProductApiName GetTryParseProductApiName(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return file => from informationFile in ProductApiInformationFile.TryParse(file, serviceDirectory)
                       select (informationFile.Parent.Name, informationFile.Parent.Parent.Parent.Name);
    }

    private static void ConfigureIsProductApiNameInSourceControl(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetArtifactFiles(builder);
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetIsApiNameInSourceControl);
    }

    private static IsProductApiNameInSourceControl GetIsApiNameInSourceControl(IServiceProvider provider)
    {
        var getArtifactFiles = provider.GetRequiredService<GetArtifactFiles>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return doesInformationFileExist;

        bool doesInformationFileExist(ApiName name, ProductName productName)
        {
            var artifactFiles = getArtifactFiles();
            var apiFile = ProductApiInformationFile.From(name, productName, serviceDirectory);

            return artifactFiles.Contains(apiFile.ToFileInfo());
        }
    }

    private static void ConfigurePutProductApi(IHostApplicationBuilder builder)
    {
        ConfigureFindProductApiDto(builder);
        ConfigurePutProductApiInApim(builder);

        builder.Services.TryAddSingleton(GetPutProductApi);
    }

    private static PutProductApi GetPutProductApi(IServiceProvider provider)
    {
        var findDto = provider.GetRequiredService<FindProductApiDto>();
        var putInApim = provider.GetRequiredService<PutProductApiInApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, productName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutProductApi))
                                       ?.AddTag("product_api.name", name)
                                       ?.AddTag("product.name", productName);

            var dtoOption = await findDto(name, productName, cancellationToken);
            await dtoOption.IterTask(async dto => await putInApim(name, dto, productName, cancellationToken));
        };
    }

    private static void ConfigureFindProductApiDto(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);
        CommonModule.ConfigureTryGetFileContents(builder);

        builder.Services.TryAddSingleton(GetFindProductApiDto);
    }

    private static FindProductApiDto GetFindProductApiDto(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var tryGetFileContents = provider.GetRequiredService<TryGetFileContents>();

        return async (name, productName, cancellationToken) =>
        {
            var informationFile = ProductApiInformationFile.From(name, productName, serviceDirectory);
            var contentsOption = await tryGetFileContents(informationFile.ToFileInfo(), cancellationToken);

            return from contents in contentsOption
                   select contents.ToObjectFromJson<ProductApiDto>();
        };
    }

    private static void ConfigurePutProductApiInApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutProductApiInApim);
    }

    private static PutProductApiInApim GetPutProductApiInApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, productName, cancellationToken) =>
        {
            logger.LogInformation("Adding API {ApiName} to product {ProductName}...", name, productName);

            await ProductApiUri.From(name, productName, serviceUri)
                               .PutDto(dto, pipeline, cancellationToken);
        };
    }

    public static void ConfigureDeleteProductApis(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryProductParseApiName(builder);
        ConfigureIsProductApiNameInSourceControl(builder);
        ConfigureDeleteProductApi(builder);

        builder.Services.TryAddSingleton(GetDeleteProductApis);
    }

    private static DeleteProductApis GetDeleteProductApis(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseProductApiName>();
        var isNameInSourceControl = provider.GetRequiredService<IsProductApiNameInSourceControl>();
        var delete = provider.GetRequiredService<DeleteProductApi>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteProductApis));

            logger.LogInformation("Deleting product apis...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(api => isNameInSourceControl(api.Name, api.ProductName) is false)
                    .Distinct()
                    .IterParallel(delete.Invoke, cancellationToken);
        };
    }

    private static void ConfigureDeleteProductApi(IHostApplicationBuilder builder)
    {
        ConfigureDeleteProductApiFromApim(builder);

        builder.Services.TryAddSingleton(GetDeleteProductApi);
    }

    private static DeleteProductApi GetDeleteProductApi(IServiceProvider provider)
    {
        var deleteFromApim = provider.GetRequiredService<DeleteProductApiFromApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, productName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteProductApi))
                                       ?.AddTag("product_api.name", name)
                                       ?.AddTag("product.name", productName);

            await deleteFromApim(name, productName, cancellationToken);
        };
    }

    private static void ConfigureDeleteProductApiFromApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteProductApiFromApim);
    }

    private static DeleteProductApiFromApim GetDeleteProductApiFromApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, productName, cancellationToken) =>
        {
            logger.LogInformation("Removing API {ApiName} from product {ProductName}...", name, productName);

            await ProductApiUri.From(name, productName, serviceUri)
                               .Delete(pipeline, cancellationToken);
        };
    }
}