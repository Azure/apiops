using common;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace extractor
{
    internal class Subscription
    {
        public static async ValueTask ExportAll(ServiceDirectory serviceDirectory, ServiceUri serviceUri, ListRestResources listRestResources, GetRestResource getRestResource, ILogger logger, IEnumerable<string>? subscriptionNamesToExport, CancellationToken cancellationToken)
        {
            await List(serviceUri, listRestResources, cancellationToken)
                    // Filter out diagnostics that should not be exported   
                    .Where(subscriptionName => ShouldExport(subscriptionName, subscriptionNamesToExport))
                    .ForEachParallel(async subscriptionName => await Export(serviceDirectory, serviceUri, subscriptionName, getRestResource, logger, cancellationToken),
                                     cancellationToken);
        }

        private static IAsyncEnumerable<SubscriptionName> List(ServiceUri serviceUri, ListRestResources listRestResources, CancellationToken cancellationToken)
        {
            var subscriptionsUri = new SubscriptionsUri(serviceUri);
            var subscriptionJsonObjects = listRestResources(subscriptionsUri.Uri, cancellationToken);
            return subscriptionJsonObjects.Select(json => json.GetStringProperty("name"))
                                        .Select(name => new SubscriptionName(name));
        }

        private static bool ShouldExport(SubscriptionName subscriptionName, IEnumerable<string>? subscriptionNamesToExport)
        {
            return subscriptionNamesToExport is null
                   || subscriptionNamesToExport.Any(subscriptionNameToExport => subscriptionNameToExport.Equals(subscriptionName.ToString(), StringComparison.OrdinalIgnoreCase));
        }

        private static async ValueTask Export(ServiceDirectory serviceDirectory, ServiceUri serviceUri, SubscriptionName subscriptionName, GetRestResource getRestResource, ILogger logger, CancellationToken cancellationToken)
        {
            var subscriptionsDirectory = new SubscriptionsDirectory(serviceDirectory);
            var subscriptionDirectory = new SubscriptionDirectory(subscriptionName, subscriptionsDirectory);

            var subscriptionsUri = new SubscriptionsUri(serviceUri);
            var subscriptionUri = new SubscriptionUri(subscriptionName, subscriptionsUri);

            await ExportInformationFile(subscriptionDirectory, subscriptionUri, subscriptionName, getRestResource, logger, cancellationToken);
        }

        private static async ValueTask ExportInformationFile(SubscriptionDirectory subscriptionDirectory, SubscriptionUri subscriptionUri, SubscriptionName subscriptionName, GetRestResource getRestResource, ILogger logger, CancellationToken cancellationToken)
        {
            var subscriptionInformationFile = new SubscriptionInformationFile(subscriptionDirectory);

            var responseJson = await getRestResource(subscriptionUri.Uri, cancellationToken);
            var subscriptionModel = SubscriptionModel.Deserialize(subscriptionName, responseJson);
            if(subscriptionModel.Name == "master" || 
                SubscriptionModel.GetGenericSubscriptionScope(subscriptionModel.Properties.Scope).Contains("/products"))
            {
                logger.LogInformation("Skipping unsupported subscription {name}", subscriptionModel.Name);
                return;
            }
            var contentJson = subscriptionModel.Serialize();

            logger.LogInformation("Writing subscription information file {filePath}...", subscriptionInformationFile.Path);
            await subscriptionInformationFile.OverwriteWithJson(contentJson, cancellationToken);
        }

    }
}
