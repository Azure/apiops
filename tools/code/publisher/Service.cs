using common;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace publisher;

internal static class Service
{
    public static async ValueTask ProcessDeletedArtifacts(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory, ServiceUri serviceUri, ListRestResources listRestResources, PutRestResource putRestResource, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await GatewayApi.ProcessDeletedArtifacts(files, configurationJson, serviceDirectory, serviceUri, listRestResources, putRestResource, deleteRestResource, logger, cancellationToken);
        await ProductApi.ProcessDeletedArtifacts(files, configurationJson, serviceDirectory, serviceUri, listRestResources, putRestResource, deleteRestResource, logger, cancellationToken);
        await ApiOperationPolicy.ProcessDeletedArtifacts(files, serviceDirectory, serviceUri, deleteRestResource, logger, cancellationToken);
        await ApiPolicy.ProcessDeletedArtifacts(files, serviceDirectory, serviceUri, deleteRestResource, logger, cancellationToken);
        await Api.ProcessDeletedArtifacts(files, configurationJson, serviceDirectory, serviceUri, putRestResource, deleteRestResource, logger, cancellationToken);
        await ProductGroup.ProcessDeletedArtifacts(files, configurationJson, serviceDirectory, serviceUri, listRestResources, putRestResource, deleteRestResource, logger, cancellationToken);
        await ProductPolicy.ProcessDeletedArtifacts(files, serviceDirectory, serviceUri, deleteRestResource, logger, cancellationToken);
        await Product.ProcessDeletedArtifacts(files, serviceDirectory, serviceUri, deleteRestResource, logger, cancellationToken);
        await ServicePolicy.ProcessDeletedArtifacts(files, serviceDirectory, serviceUri, deleteRestResource, logger, cancellationToken);
        await Diagnostic.ProcessDeletedArtifacts(files, serviceDirectory, serviceUri, deleteRestResource, logger, cancellationToken);
        await Logger.ProcessDeletedArtifacts(files, serviceDirectory, serviceUri, deleteRestResource, logger, cancellationToken);
        await Backend.ProcessDeletedArtifacts(files, serviceDirectory, serviceUri, deleteRestResource, logger, cancellationToken);
        await Gateway.ProcessDeletedArtifacts(files, serviceDirectory, serviceUri, deleteRestResource, logger, cancellationToken);
        await ApiVersionSet.ProcessDeletedArtifacts(files, serviceDirectory, serviceUri, deleteRestResource, logger, cancellationToken);
        await NamedValue.ProcessDeletedArtifacts(files, serviceDirectory, serviceUri, deleteRestResource, logger, cancellationToken);
    }

    public static async ValueTask ProcessArtifactsToPut(IReadOnlyCollection<FileInfo> files, bool putAllConfigurationArtifacts, JsonObject configurationJson, ServiceDirectory serviceDirectory, ServiceUri serviceUri, ListRestResources listRestResources, PutRestResource putRestResource, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await NamedValue.ProcessArtifactsToPut(files, putAllConfigurationArtifacts, configurationJson, serviceDirectory, serviceUri, putRestResource, logger, cancellationToken);
        await ApiVersionSet.ProcessArtifactsToPut(files, putAllConfigurationArtifacts, configurationJson, serviceDirectory, serviceUri, putRestResource, logger, cancellationToken);
        await Gateway.ProcessArtifactsToPut(files, putAllConfigurationArtifacts, configurationJson, serviceDirectory, serviceUri, putRestResource, logger, cancellationToken);
        await Backend.ProcessArtifactsToPut(files, putAllConfigurationArtifacts, configurationJson, serviceDirectory, serviceUri, putRestResource, logger, cancellationToken);
        await Logger.ProcessArtifactsToPut(files, putAllConfigurationArtifacts, configurationJson, serviceDirectory, serviceUri, putRestResource, logger, cancellationToken);
        await Diagnostic.ProcessArtifactsToPut(files, putAllConfigurationArtifacts, configurationJson, serviceDirectory, serviceUri, putRestResource, logger, cancellationToken);
        await ServicePolicy.ProcessArtifactsToPut(files, putAllConfigurationArtifacts, configurationJson, serviceDirectory, serviceUri, putRestResource, logger, cancellationToken);
        await Product.ProcessArtifactsToPut(files, putAllConfigurationArtifacts, configurationJson, serviceDirectory, serviceUri, putRestResource, logger, cancellationToken);
        await ProductPolicy.ProcessArtifactsToPut(files, putAllConfigurationArtifacts, configurationJson, serviceDirectory, serviceUri, putRestResource, logger, cancellationToken);
        await ProductGroup.ProcessArtifactsToPut(files, configurationJson, serviceDirectory, serviceUri, listRestResources, putRestResource, deleteRestResource, logger, cancellationToken);
        await Api.ProcessArtifactsToPut(files, putAllConfigurationArtifacts, configurationJson, serviceDirectory, serviceUri, putRestResource, logger, cancellationToken);
        await ApiPolicy.ProcessArtifactsToPut(files, putAllConfigurationArtifacts, configurationJson, serviceDirectory, serviceUri, putRestResource, logger, cancellationToken);
        await ApiOperationPolicy.ProcessArtifactsToPut(files, putAllConfigurationArtifacts, configurationJson, serviceDirectory, serviceUri, putRestResource, logger, cancellationToken);
        await ProductApi.ProcessArtifactsToPut(files, configurationJson, serviceDirectory, serviceUri, listRestResources, putRestResource, deleteRestResource, logger, cancellationToken);
        await GatewayApi.ProcessArtifactsToPut(files, configurationJson, serviceDirectory, serviceUri, listRestResources, putRestResource, deleteRestResource, logger, cancellationToken);
    }
}