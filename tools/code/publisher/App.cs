using common;
using LanguageExt;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace publisher;

internal delegate ValueTask RunPublisher(CancellationToken cancellationToken);

file delegate ValueTask PublishFile(FileInfo file, CancellationToken cancellationToken);

file sealed class RunPublisherHandler(ProcessNamedValuesToPut processNamedValuesToPut,
                                      ProcessDeletedNamedValues processDeletedNamedValues,
                                      ProcessBackendsToPut processBackendsToPut,
                                      ProcessDeletedBackends processDeletedBackends,
                                      ProcessPolicyFragmentsToPut processPolicyFragmentsToPut,
                                      ProcessDeletedPolicyFragments processDeletedPolicyFragments,
                                      GetPublisherFiles getPublisherFiles,
                                      PublishFile publishFile,
                                      ILoggerFactory loggerFactory)
{
    private readonly ILogger logger = Common.CreateLogger(loggerFactory);

    public async ValueTask Handle(CancellationToken cancellationToken)
    {
        await processNamedValuesToPut(cancellationToken);
        await processBackendsToPut(cancellationToken);
        await processPolicyFragmentsToPut(cancellationToken);

        await ProcessPublisherFiles(cancellationToken);

        await processDeletedPolicyFragments(cancellationToken);
        await processDeletedBackends(cancellationToken);
        await processDeletedNamedValues(cancellationToken);

        logger.LogInformation("Publisher completed.");
    }

    private async ValueTask ProcessPublisherFiles(CancellationToken cancellationToken)
    {
        var files = getPublisherFiles();

        files.Iter(file => logger.LogDebug("Found file {FilePath}.", file.FullName));

        logger.LogInformation("Found {Count} files in scope for publisher.", files.Count);

        await files.IterParallel(publishFile.Invoke, cancellationToken);
    }
}

file sealed class PublishFileHandler(FindTagAction findTagAction,
                                     FindGatewayAction findGatewayAction,
                                     FindVersionSetAction findVersionSetAction,
                                     FindLoggerAction findLoggerAction,
                                     FindDiagnosticAction findDiagnosticAction,
                                     FindServicePolicyAction findServicePolicyAction,
                                     FindProductAction findProductAction,
                                     FindProductPolicyAction findProductPolicyAction,
                                     FindGroupAction findGroupAction,
                                     FindProductGroupAction findProductGroupAction,
                                     FindProductTagAction findProductTagAction,
                                     FindProductApiAction findProductApiAction,
                                     FindSubscriptionAction findSubscriptionAction,
                                     FindApiAction findApiAction,
                                     FindApiPolicyAction findApiPolicyAction,
                                     FindApiTagAction findApiTagAction,
                                     FindApiOperationPolicyAction findApiOperationPolicyAction,
                                     FindGatewayApiAction findGatewayApiAction)
{
    /// <summary>
    /// Run the first publisher action that can handle the file.
    /// </summary>
    public async ValueTask Handle(FileInfo file, CancellationToken cancellationToken) =>
        await FindPublisherAction(file).IterTask(async action => await action(cancellationToken));

    private Option<PublisherAction> FindPublisherAction(FileInfo file) =>
        findTagAction(file)
        | findGatewayAction(file)
        | findVersionSetAction(file)
        | findLoggerAction(file)
        | findDiagnosticAction(file)
        | findServicePolicyAction(file)
        | findProductAction(file)
        | findProductPolicyAction(file)
        | findGroupAction(file)
        | findProductGroupAction(file)
        | findProductTagAction(file)
        | findProductApiAction(file)
        | findSubscriptionAction(file)
        | findApiAction(file)
        | findApiPolicyAction(file)
        | findApiTagAction(file)
        | findApiOperationPolicyAction(file)
        | findGatewayApiAction(file);
}

internal static class AppServices
{
    public static void ConfigureRunPublisher(IServiceCollection services)
    {
        NamedValueServices.ConfigureProcessNamedValuesToPut(services);
        NamedValueServices.ConfigureProcessDeletedNamedValues(services);
        BackendServices.ConfigureProcessBackendsToPut(services);
        BackendServices.ConfigureProcessDeletedBackends(services);
        PolicyFragmentServices.ConfigureProcessPolicyFragmentsToPut(services);
        PolicyFragmentServices.ConfigureProcessDeletedPolicyFragments(services);
        ConfigurePublishFile(services);

        services.TryAddSingleton<RunPublisherHandler>();
        services.TryAddSingleton<RunPublisher>(provider => provider.GetRequiredService<RunPublisherHandler>().Handle);
    }

    private static void ConfigurePublishFile(IServiceCollection services)
    {
        TagServices.ConfigureFindTagAction(services);
        GatewayServices.ConfigureFindGatewayAction(services);
        VersionSetServices.ConfigureFindVersionSetAction(services);
        LoggerServices.ConfigureFindLoggerAction(services);
        DiagnosticServices.ConfigureFindDiagnosticAction(services);
        PolicyFragmentServices.ConfigureFindPolicyFragmentAction(services);
        ServicePolicyServices.ConfigureFindServicePolicyAction(services);
        ProductServices.ConfigureFindProductAction(services);
        ProductPolicyServices.ConfigureFindProductPolicyAction(services);
        GroupServices.ConfigureFindGroupAction(services);
        ProductGroupServices.ConfigureFindProductGroupAction(services);
        ProductTagServices.ConfigureFindProductTagAction(services);
        ProductApiServices.ConfigureFindProductApiAction(services);
        ApiServices.ConfigureFindApiAction(services);
        ApiPolicyServices.ConfigureFindApiPolicyAction(services);
        ApiTagServices.ConfigureFindApiTagAction(services);
        ApiOperationPolicyServices.ConfigureFindApiOperationPolicyAction(services);
        GatewayApiServices.ConfigureFindGatewayApiAction(services);
        SubscriptionServices.ConfigureFindSubscriptionAction(services);

        services.TryAddSingleton<PublishFileHandler>();
        services.TryAddSingleton<PublishFile>(provider => provider.GetRequiredService<PublishFileHandler>().Handle);
    }
}

file static class Common
{
    public static ILogger CreateLogger(ILoggerFactory loggerFactory) =>
        loggerFactory.CreateLogger("RunPublisher");
}