using Azure.Core.Pipeline;
using common;
using common.tests;
using CsCheck;
using Flurl;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Polly;
using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace integration.tests;

internal delegate ValueTask DeleteAllTestServices(CancellationToken cancellationToken);
internal delegate ValueTask PutService(ServiceName name, ServiceSku sku, ServiceModel model, CancellationToken cancellationToken);
internal delegate ValueTask DeleteService(ServiceName name, CancellationToken cancellationToken);
internal delegate Uri GetServiceUri(ServiceName name);

internal abstract record ServiceSku
{
#pragma warning disable CA1812 // Avoid uninstantiated internal classes
    internal sealed record Consumption : ServiceSku;
#pragma warning restore CA1812 // Avoid uninstantiated internal classes
    internal sealed record Basic : ServiceSku;
    internal sealed record Standard : ServiceSku;
}

internal static class ServiceModule
{
    public static string TestServiceNamePrefix { get; } = "apiopsintgrtst";

    public static void ConfigureDeleteAllTestServices(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureHttpPipeline(builder);
        common.ServiceModule.ConfigureServiceProviderUri(builder);

        builder.Services.TryAddSingleton(GetDeleteAllTestServices);
    }

    private static DeleteAllTestServices GetDeleteAllTestServices(IServiceProvider provider)
    {
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var serviceProviderUri = provider.GetRequiredService<ServiceProviderUri>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity("service.delete_all_test");

            await pipeline.ListJsonObjects(serviceProviderUri.ToUri(), cancellationToken)
                          .Choose(jsonObject => jsonObject.GetStringProperty("name")
                                                          .ToFin()
                                                          .ToOption())
                          .Where(name => name.StartsWith(TestServiceNamePrefix, StringComparison.OrdinalIgnoreCase))
                          .Select(name => serviceProviderUri.ToUri().AppendPathSegment(name).ToUri())
                          .IterParallel(async uri => await DeleteService(pipeline, uri, cancellationToken),
                                        cancellationToken);
        };
    }

    private static async ValueTask DeleteService(HttpPipeline pipeline, Uri uri, CancellationToken cancellationToken)
    {
        var either = await pipeline.TryDeleteResource(uri, waitForCompletion: false, cancellationToken);

        either.IfLeft(response =>
        {
            using (response)
            {
                if (response.Status != (int)HttpStatusCode.Conflict)
                {
                    throw response.ToHttpRequestException(uri);
                }
            }
        });
    }

    public static void ConfigurePutService(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureHttpPipeline(builder);
        ConfigureGetServiceUri(builder);

        builder.Services.TryAddSingleton(GetPutService);
    }

    private static PutService GetPutService(IServiceProvider provider)
    {
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var getServiceUri = provider.GetRequiredService<GetServiceUri>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        var httpResiliencePipeline =
            new ResiliencePipelineBuilder()
                .AddRetry(new()
                {
                    ShouldHandle = async arguments =>
                    {
                        await ValueTask.CompletedTask;

                        return arguments.Outcome.Exception?.Message?.Contains("is transitioning at this time", StringComparison.OrdinalIgnoreCase) ?? false;
                    },
                    Delay = TimeSpan.FromSeconds(5),
                    BackoffType = DelayBackoffType.Linear,
                    MaxRetryAttempts = 100
                })
                .AddTimeout(TimeSpan.FromMinutes(3))
                .Build();

        var statusResiliencePipeline =
            new ResiliencePipelineBuilder<string>()
                .AddRetry(new()
                {
                    ShouldHandle = async arguments =>
                    {
                        await ValueTask.CompletedTask;

                        var exceptionMessage = arguments.Outcome.Exception?.Message;
                        var result = arguments.Outcome.Result;

                        return (exceptionMessage, result) switch
                        {
                            (not null, _) when exceptionMessage.Contains("is transitioning at this time", StringComparison.OrdinalIgnoreCase) => true,
                            (_, not null) when result.Equals("Succeeded", StringComparison.OrdinalIgnoreCase) => false,
                            _ => true
                        };
                    },
                    Delay = TimeSpan.FromSeconds(5),
                    BackoffType = DelayBackoffType.Linear,
                    MaxRetryAttempts = 100
                })
                .AddTimeout(TimeSpan.FromMinutes(3))
                .Build();

        return async (name, sku, model, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity("service.put")
                                       ?.AddTag("service_name", name);

            var uri = getServiceUri(name);

            var body = BinaryData.FromObjectAsJson(new
            {
                location = "eastus2",
                sku = new
                {
                    name = sku switch
                    {
                        ServiceSku.Standard => "StandardV2",
                        ServiceSku.Basic => "BasicV2",
                        ServiceSku.Consumption => "Consumption",
                        _ => throw new ArgumentOutOfRangeException(nameof(sku), sku, "SKU is invalid.")
                    },
                    capacity = sku switch
                    {
                        ServiceSku.Consumption => 0,
                        _ => 1
                    }
                },
                identity = new
                {
                    type = "SystemAssigned"
                },
                properties = new
                {
                    publisherEmail = "admin@contoso.com",
                    publisherName = "Contoso"
                }
            });

            await httpResiliencePipeline.ExecuteAsync(async cancellationToken => await pipeline.PutContent(uri, body, cancellationToken), cancellationToken);

            // Wait until the service is successfully provisioned
            await statusResiliencePipeline.ExecuteAsync(async cancellationToken =>
            {
                var content = await pipeline.GetJsonObject(uri, cancellationToken);

                return content.GetJsonObjectProperty("properties")
                              .Bind(properties => properties.GetStringProperty("provisioningState"))
                              .IfFail(_ => string.Empty);
            }, cancellationToken);
        };
    }

    public static void ConfigureDeleteService(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureHttpPipeline(builder);
        ConfigureGetServiceUri(builder);

        builder.Services.TryAddSingleton(GetDeleteService);
    }

    private static DeleteService GetDeleteService(IServiceProvider provider)
    {
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var getServiceUri = provider.GetRequiredService<GetServiceUri>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity("service.delete")
                                       ?.AddTag("service_name", name);

            var uri = getServiceUri(name);

            await DeleteService(pipeline, uri, cancellationToken);
        };
    }

    public static void ConfigureGetServiceUri(IHostApplicationBuilder builder)
    {
        common.ServiceModule.ConfigureServiceProviderUri(builder);

        builder.Services.TryAddSingleton(GetGetServiceUri);
    }

    private static GetServiceUri GetGetServiceUri(IServiceProvider provider)
    {
        var serviceProviderUri = provider.GetRequiredService<ServiceProviderUri>();

        return name => serviceProviderUri.ToUri()
                                         .AppendPathSegment(name.ToString())
                                         .ToUri();
    }
}
