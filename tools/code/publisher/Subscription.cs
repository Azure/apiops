using common;
using Microsoft.Extensions.Logging;
using MoreLinq;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace publisher;
internal static class Subscription
{
    public static async ValueTask ProcessDeletedArtifacts(IReadOnlyCollection<FileInfo> files, ServiceDirectory serviceDirectory, ServiceUri serviceUri, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await GetSubscriptionInformationFiles(files, serviceDirectory)
                .Select(GetSubscriptionName)
                .ForEachParallel(async subscriptionName => await Delete(subscriptionName, serviceUri, deleteRestResource, logger, cancellationToken),
                                 cancellationToken);
    }

    private static IEnumerable<SubscriptionInformationFile> GetSubscriptionInformationFiles(IReadOnlyCollection<FileInfo> files, ServiceDirectory serviceDirectory)
    {
        return files.Choose(file => TryGetSubscriptionInformationFile(file, serviceDirectory));
    }

    private static SubscriptionInformationFile? TryGetSubscriptionInformationFile(FileInfo? file, ServiceDirectory serviceDirectory)
    {
        if (file is null || file.Name.Equals(SubscriptionInformationFile.Name) is false)
        {
            return null;
        }

        var subscriptionDirectory = TryGetSubscriptionDirectory(file.Directory, serviceDirectory);

        return subscriptionDirectory is null
                ? null
                : new SubscriptionInformationFile(subscriptionDirectory);
    }

    private static SubscriptionDirectory? TryGetSubscriptionDirectory(DirectoryInfo? directory, ServiceDirectory serviceDirectory)
    {
        if (directory is null)
        {
            return null;
        }

        var subscriptionsDirectory = TryGetSubscriptionsDirectory(directory.Parent, serviceDirectory);
        if (subscriptionsDirectory is null)
        {
            return null;
        }

        var subscriptionName = new SubscriptionName(directory.Name);
        return new SubscriptionDirectory(subscriptionName, subscriptionsDirectory);
    }

    private static SubscriptionsDirectory? TryGetSubscriptionsDirectory(DirectoryInfo? directory, ServiceDirectory serviceDirectory)
    {
        return directory is null
            || directory.Name.Equals(SubscriptionsDirectory.Name) is false
            || serviceDirectory.PathEquals(directory.Parent) is false
            ? null
            : new SubscriptionsDirectory(serviceDirectory);
    }

    private static SubscriptionName GetSubscriptionName(SubscriptionInformationFile file)
    {
        return new(file.SubscriptionDirectory.GetName());
    }

    private static async ValueTask Delete(SubscriptionName subscriptionName, ServiceUri serviceUri, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        var uri = GetSubscriptionUri(subscriptionName, serviceUri);

        logger.LogInformation("Deleting subscription {subscriptionName}...", subscriptionName);
        await deleteRestResource(uri.Uri, cancellationToken);
    }

    private static SubscriptionUri GetSubscriptionUri(SubscriptionName subscriptionName, ServiceUri serviceUri)
    {
        var subscriptionsUri = new SubscriptionsUri(serviceUri);
        return new SubscriptionUri(subscriptionName, subscriptionsUri);
    }

    public static async ValueTask ProcessArtifactsToPut(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory, ServiceUri serviceUri, PutRestResource putRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await GetArtifactsToPut(files, configurationJson, serviceDirectory)
                .ForEachAwaitAsync(async artifact => await PutSubscription(artifact.Name, artifact.Json, serviceUri, putRestResource, logger, cancellationToken));
    }

    private static IEnumerable<(SubscriptionName Name, JsonObject Json)> GetArtifactsToPut(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory)
    {
        var configurationArtifacts = GetConfigurationSubscriptions(configurationJson);

        return GetSubscriptionInformationFiles(files, serviceDirectory)
                .Select(file => (Name: GetSubscriptionName(file), Json: file.ReadAsJsonObject()))
                .LeftJoin(configurationArtifacts,
                          keySelector: artifact => artifact.Name,
                          bothSelector: (fileArtifact, configurationArtifact) => (fileArtifact.Name, fileArtifact.Json.Merge(configurationArtifact.Json)));
    }

    private static IEnumerable<(SubscriptionName Name, JsonObject Json)> GetConfigurationSubscriptions(JsonObject configurationJson)
    {
        return configurationJson.TryGetJsonArrayProperty("subscriptions")
                                .IfNullEmpty()
                                .Choose(node => node as JsonObject)
                                .Choose(jsonObject =>
                                {
                                    var name = jsonObject.TryGetStringProperty("name");
                                    return name is null
                                            ? null as (SubscriptionName, JsonObject)?
                                            : (new SubscriptionName(name), jsonObject);
                                });
    }

    private static async ValueTask PutSubscription(SubscriptionName subscriptionName, JsonObject json, ServiceUri serviceUri, PutRestResource putRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        var subscription = SubscriptionModel.Deserialize(subscriptionName, json);
        logger.LogInformation("Putting subscription {subscriptionName}...", subscriptionName);
        var uri = GetSubscriptionUri(subscriptionName, serviceUri);
        await putRestResource(uri.Uri, json, cancellationToken);
    }
}

