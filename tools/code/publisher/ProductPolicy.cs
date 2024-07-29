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

public delegate ValueTask PutProductPolicies(CancellationToken cancellationToken);
public delegate Option<(ProductPolicyName Name, ProductName ProductName)> TryParseProductPolicyName(FileInfo file);
public delegate bool IsProductPolicyNameInSourceControl(ProductPolicyName name, ProductName productName);
public delegate ValueTask PutProductPolicy(ProductPolicyName name, ProductName productName, CancellationToken cancellationToken);
public delegate ValueTask<Option<ProductPolicyDto>> FindProductPolicyDto(ProductPolicyName name, ProductName productName, CancellationToken cancellationToken);
public delegate ValueTask PutProductPolicyInApim(ProductPolicyName name, ProductPolicyDto dto, ProductName productName, CancellationToken cancellationToken);
public delegate ValueTask DeleteProductPolicies(CancellationToken cancellationToken);
public delegate ValueTask DeleteProductPolicy(ProductPolicyName name, ProductName productName, CancellationToken cancellationToken);
public delegate ValueTask DeleteProductPolicyFromApim(ProductPolicyName name, ProductName productName, CancellationToken cancellationToken);

internal static class ProductPolicyModule
{
    public static void ConfigurePutProductPolicies(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseProductPolicyName(builder);
        ConfigureIsProductPolicyNameInSourceControl(builder);
        ConfigurePutProductPolicy(builder);

        builder.Services.TryAddSingleton(GetPutProductPolicies);
    }

    private static PutProductPolicies GetPutProductPolicies(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseProductPolicyName>();
        var isNameInSourceControl = provider.GetRequiredService<IsProductPolicyNameInSourceControl>();
        var put = provider.GetRequiredService<PutProductPolicy>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(PutProductPolicies));

            logger.LogInformation("Putting product policies...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(policy => isNameInSourceControl(policy.Name, policy.ProductName))
                    .Distinct()
                    .IterParallel(put.Invoke, cancellationToken);
        };
    }

    private static void ConfigureTryParseProductPolicyName(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetTryParseProductPolicyName);
    }

    private static TryParseProductPolicyName GetTryParseProductPolicyName(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return file => from policyFile in ProductPolicyFile.TryParse(file, serviceDirectory)
                       select (policyFile.Name, policyFile.Parent.Name);
    }

    private static void ConfigureIsProductPolicyNameInSourceControl(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetArtifactFiles(builder);
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetIsProductPolicyNameInSourceControl);
    }

    private static IsProductPolicyNameInSourceControl GetIsProductPolicyNameInSourceControl(IServiceProvider provider)
    {
        var getArtifactFiles = provider.GetRequiredService<GetArtifactFiles>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return doesPolicyFileExist;

        bool doesPolicyFileExist(ProductPolicyName name, ProductName productName)
        {
            var artifactFiles = getArtifactFiles();
            var policyFile = ProductPolicyFile.From(name, productName, serviceDirectory);

            return artifactFiles.Contains(policyFile.ToFileInfo());
        }
    }

    private static void ConfigurePutProductPolicy(IHostApplicationBuilder builder)
    {
        ConfigureFindProductPolicyDto(builder);
        ConfigurePutProductPolicyInApim(builder);

        builder.Services.TryAddSingleton(GetPutProductPolicy);
    }

    private static PutProductPolicy GetPutProductPolicy(IServiceProvider provider)
    {
        var findDto = provider.GetRequiredService<FindProductPolicyDto>();
        var putInApim = provider.GetRequiredService<PutProductPolicyInApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, productName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutProductPolicy))
                                       ?.AddTag("product_policy.name", name)
                                       ?.AddTag("product.name", productName);

            var dtoOption = await findDto(name, productName, cancellationToken);
            await dtoOption.IterTask(async dto => await putInApim(name, dto, productName, cancellationToken));
        };
    }

    private static void ConfigureFindProductPolicyDto(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);
        CommonModule.ConfigureTryGetFileContents(builder);

        builder.Services.TryAddSingleton(GetFindProductPolicyDto);
    }

    private static FindProductPolicyDto GetFindProductPolicyDto(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var tryGetFileContents = provider.GetRequiredService<TryGetFileContents>();

        return async (name, productName, cancellationToken) =>
        {
            var contentsOption = await tryGetPolicyContents(name, productName, cancellationToken);

            return from contents in contentsOption
                   select new ProductPolicyDto
                   {
                       Properties = new ProductPolicyDto.ProductPolicyContract
                       {
                           Format = "rawxml",
                           Value = contents.ToString()
                       }
                   };
        };

        async ValueTask<Option<BinaryData>> tryGetPolicyContents(ProductPolicyName name, ProductName productName, CancellationToken cancellationToken)
        {
            var policyFile = ProductPolicyFile.From(name, productName, serviceDirectory);

            return await tryGetFileContents(policyFile.ToFileInfo(), cancellationToken);
        }
    }

    private static void ConfigurePutProductPolicyInApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutProductPolicyInApim);
    }

    private static PutProductPolicyInApim GetPutProductPolicyInApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, productName, cancellationToken) =>
        {
            logger.LogInformation("Putting policy {ProductPolicyName} for product {ProductName}...", name, productName);

            await ProductPolicyUri.From(name, productName, serviceUri)
                                  .PutDto(dto, pipeline, cancellationToken);
        };
    }

    public static void ConfigureDeleteProductPolicies(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseProductPolicyName(builder);
        ConfigureIsProductPolicyNameInSourceControl(builder);
        ConfigureDeleteProductPolicy(builder);

        builder.Services.TryAddSingleton(GetDeleteProductPolicies);
    }

    private static DeleteProductPolicies GetDeleteProductPolicies(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseProductPolicyName>();
        var isNameInSourceControl = provider.GetRequiredService<IsProductPolicyNameInSourceControl>();
        var delete = provider.GetRequiredService<DeleteProductPolicy>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteProductPolicies));

            logger.LogInformation("Deleting product policies...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(policy => isNameInSourceControl(policy.Name, policy.ProductName) is false)
                    .Distinct()
                    .IterParallel(delete.Invoke, cancellationToken);
        };
    }

    private static void ConfigureDeleteProductPolicy(IHostApplicationBuilder builder)
    {
        ConfigureDeleteProductPolicyFromApim(builder);

        builder.Services.TryAddSingleton(GetDeleteProductPolicy);
    }

    private static DeleteProductPolicy GetDeleteProductPolicy(IServiceProvider provider)
    {
        var deleteFromApim = provider.GetRequiredService<DeleteProductPolicyFromApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, productName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteProductPolicy))
                                       ?.AddTag("product_policy.name", name)
                                       ?.AddTag("product.name", productName);

            await deleteFromApim(name, productName, cancellationToken);
        };
    }

    private static void ConfigureDeleteProductPolicyFromApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteProductPolicyFromApim);
    }

    private static DeleteProductPolicyFromApim GetDeleteProductPolicyFromApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, productName, cancellationToken) =>
        {
            logger.LogInformation("Deleting policy {ProductPolicyName} from product {ProductName}...", name, productName);

            await ProductPolicyUri.From(name, productName, serviceUri)
                                  .Delete(pipeline, cancellationToken);
        };
    }
}