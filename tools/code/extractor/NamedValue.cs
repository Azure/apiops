using common;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal static class NamedValue
{
    public static async ValueTask ExportAll(ServiceDirectory serviceDirectory, ServiceUri serviceUri, ListRestResources listRestResources, GetRestResource getRestResource, CancellationToken cancellationToken)
    {
        await List(serviceUri, listRestResources, cancellationToken)
                .ForEachParallel(async namedValueName => await Export(serviceDirectory,
                                                                      serviceUri,
                                                                      namedValueName,
                                                                      getRestResource,
                                                                      cancellationToken),
                                 cancellationToken);
    }

    private static IAsyncEnumerable<NamedValueName> List(ServiceUri serviceUri, ListRestResources listRestResources, CancellationToken cancellationToken)
    {
        var namedValuesUri = new NamedValuesUri(serviceUri);
        var namedValueJsonObjects = listRestResources(namedValuesUri.Uri, cancellationToken);
        return namedValueJsonObjects.Select(json => json.GetStringProperty("name"))
                                    .Select(name => new NamedValueName(name));
    }

    private static async ValueTask Export(ServiceDirectory serviceDirectory, ServiceUri serviceUri, NamedValueName namedValueName, GetRestResource getRestResource, CancellationToken cancellationToken)
    {
        var namedValuesDirectory = new NamedValuesDirectory(serviceDirectory);
        var namedValueDirectory = new NamedValueDirectory(namedValueName, namedValuesDirectory);

        var namedValuesUri = new NamedValuesUri(serviceUri);
        var namedValueUri = new NamedValueUri(namedValueName, namedValuesUri);

        await ExportInformationFile(namedValueDirectory, namedValueUri, namedValueName, getRestResource, cancellationToken);
    }

    private static async ValueTask ExportInformationFile(NamedValueDirectory namedValueDirectory, NamedValueUri namedValueUri, NamedValueName namedValueName, GetRestResource getRestResource, CancellationToken cancellationToken)
    {
        var namedValueInformationFile = new NamedValueInformationFile(namedValueDirectory);

        var responseJson = await getRestResource(namedValueUri.Uri, cancellationToken);
        var namedValueModel = NamedValueModel.Deserialize(namedValueName, responseJson);
        var contentJson = namedValueModel.Serialize();

        await namedValueInformationFile.OverwriteWithJson(contentJson, cancellationToken);
    }
}