using common;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace publisher;

internal static class ProductPolicy
{
    public static async ValueTask ProcessDeletedArtifacts(IReadOnlyCollection<FileInfo> files, ServiceDirectory serviceDirectory, ServiceUri serviceUri, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await GetPolicyFiles(files, serviceDirectory)
                .Select(file => (PolicyName: GetPolicyName(file), ProductName: GetProductName(file)))
                .ForEachParallel(async policy => await Delete(policy.PolicyName, policy.ProductName, serviceUri, deleteRestResource, logger, cancellationToken),
                                 cancellationToken);
    }

    private static IEnumerable<ProductPolicyFile> GetPolicyFiles(IReadOnlyCollection<FileInfo> files, ServiceDirectory serviceDirectory)
    {
        return files.Choose(file => TryGetProductPolicyFile(file, serviceDirectory));
    }

    private static ProductPolicyFile? TryGetProductPolicyFile(FileInfo file, ServiceDirectory serviceDirectory)
    {
        if (file is null || file.Name.EndsWith("xml") is false)
        {
            return null;
        }

        var productDirectory = Product.TryGetProductDirectory(file.Directory, serviceDirectory);
        if (productDirectory is null)
        {
            return null;
        }

        var policyNameString = Path.GetFileNameWithoutExtension(file.FullName);
        var policyName = new ProductPolicyName(policyNameString);
        return new ProductPolicyFile(policyName, productDirectory);
    }

    private static ProductPolicyName GetPolicyName(ProductPolicyFile file)
    {
        return new(file.GetNameWithoutExtensions());
    }

    private static ProductName GetProductName(ProductPolicyFile file)
    {
        return new(file.ProductDirectory.GetName());
    }

    private static async ValueTask Delete(ProductPolicyName policyName, ProductName productName, ServiceUri serviceUri, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        logger.LogInformation("Deleting policy {policyName} in product {productName}...", policyName, productName);

        var uri = GetProductPolicyUri(policyName, productName, serviceUri);
        await deleteRestResource(uri.Uri, cancellationToken);
    }

    private static ProductPolicyUri GetProductPolicyUri(ProductPolicyName policyName, ProductName productName, ServiceUri serviceUri)
    {
        var productUri = Product.GetProductUri(productName, serviceUri);
        var policiesUri = new ProductPoliciesUri(productUri);
        return new ProductPolicyUri(policyName, policiesUri);
    }

    public static async ValueTask ProcessArtifactsToPut(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory, ServiceUri serviceUri, PutRestResource putRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await GetArtifactsToPut(files, configurationJson, serviceDirectory, cancellationToken)
                .ForEachParallel(async artifact => await PutPolicy(artifact.PolicyName, artifact.ProductName, artifact.Json, serviceUri, putRestResource, logger, cancellationToken),
                                 cancellationToken);
    }

    private static async IAsyncEnumerable<(ProductPolicyName PolicyName, ProductName ProductName, JsonObject Json)> GetArtifactsToPut(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var fileArtifacts = await GetFilePolicies(files, serviceDirectory, cancellationToken).ToListAsync(cancellationToken);
        var configurationArtifacts = GetConfigurationPolicies(configurationJson);
        var artifacts = fileArtifacts.LeftJoin(configurationArtifacts,
                                               keySelector: artifact => (artifact.PolicyName, artifact.ProductName),
                                               bothSelector: (fileArtifact, configurationArtifact) => (fileArtifact.PolicyName, fileArtifact.ProductName, fileArtifact.Json.Merge(configurationArtifact.Json)));

        foreach (var artifact in artifacts)
        {
            yield return artifact;
        }
    }

    private static IAsyncEnumerable<(ProductPolicyName PolicyName, ProductName ProductName, JsonObject Json)> GetFilePolicies(IReadOnlyCollection<FileInfo> files, ServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        return GetPolicyFiles(files, serviceDirectory)
                .ToAsyncEnumerable()
                .SelectAwaitWithCancellation(async (file, token) =>
                {
                    var policyText = await file.ReadAsString(cancellationToken);
                    var policyJson = new JsonObject
                    {
                        ["properties"] = new JsonObject
                        {
                            ["format"] = "rawxml",
                            ["value"] = policyText
                        }
                    };
                    return (GetPolicyName(file), GetProductName(file), policyJson);
                });
    }

    private static IEnumerable<(ProductPolicyName PolicyName, ProductName ProductName, JsonObject Json)> GetConfigurationPolicies(JsonObject configurationJson)
    {
        return GetConfigurationProducts(configurationJson)
                .SelectMany(product => GetConfigurationPolicies(product.ProductName, product.Json));
    }

    private static IEnumerable<(ProductName ProductName, JsonObject Json)> GetConfigurationProducts(JsonObject configurationJson)
    {
        return configurationJson.TryGetJsonArrayProperty("products")
                                .IfNullEmpty()
                                .Choose(node => node as JsonObject)
                                .Choose(productJsonObject =>
                                {
                                    var name = productJsonObject.TryGetStringProperty("name");
                                    return name is null
                                            ? null as (ProductName, JsonObject)?
                                            : (new ProductName(name), productJsonObject);
                                });
    }

    private static IEnumerable<(ProductPolicyName PolicyName, ProductName ProductName, JsonObject Json)> GetConfigurationPolicies(ProductName productName, JsonObject configurationOperationJson)
    {
        return configurationOperationJson.TryGetJsonArrayProperty("policies")
                                         .IfNullEmpty()
                                         .Choose(node => node as JsonObject)
                                         .Choose(policyJsonObject =>
                                         {
                                             var name = policyJsonObject.TryGetStringProperty("name");
                                             return name is null
                                                     ? null as (ProductPolicyName, ProductName, JsonObject)?
                                                     : (new ProductPolicyName(name), productName, policyJsonObject);
                                         });
    }

    private static async ValueTask PutPolicy(ProductPolicyName policyName, ProductName productName, JsonObject json, ServiceUri serviceUri, PutRestResource putRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting policy {policyName} in product {productName}...", policyName, productName);

        var policyUri = GetProductPolicyUri(policyName, productName, serviceUri);
        await putRestResource(policyUri.Uri, json, cancellationToken);
    }
}