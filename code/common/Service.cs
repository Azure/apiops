using Flurl;
using LanguageExt;
using LanguageExt.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using System;
using System.IO;

namespace common;

public sealed record ServiceName : ResourceName
{
    private ServiceName(string value) : base(value) { }

    public static Fin<ServiceName> From(string? value) =>
        string.IsNullOrWhiteSpace(value)
        ? Fin<ServiceName>.Fail($"{typeof(ServiceName)} cannot be null or whitespace.")
        : new ServiceName(value);
}

public sealed record ServiceUri : ResourceUri
{
    private ServiceUri(Uri value) : base(value) { }

    public static ServiceUri From(Uri value) =>
        new(value);
}

public sealed record ServiceProviderUri : ResourceUri
{
    private ServiceProviderUri(Uri value) : base(value) { }

    public static ServiceProviderUri From(Uri value) =>
        new(value);
}

public sealed record ServiceDirectory : ResourceDirectory
{
    private ServiceDirectory(string path) : base(path) { }

    public static ServiceDirectory From(DirectoryInfo value) =>
        new(value.FullName);

    public static Fin<ServiceDirectory> From(string path) =>
        string.IsNullOrWhiteSpace(path)
        ? Fin<ServiceDirectory>.Fail($"Service directory path cannot be null or whitespace.")
        : new ServiceDirectory(path);
}

public static class ServiceModule
{
    public static void ConfigureServiceName(IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(GetServiceName);
    }

    private static ServiceName GetServiceName(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();

        var nameOption = configuration.GetValue("API_MANAGEMENT_SERVICE_NAME")
                         | configuration.GetValue("apimServiceName");

        var result = from nameString in nameOption.ToFin(Error.New("Service name was not found. Ensure 'API_MANAGEMENT_SERVICE_NAME' or 'apimServiceName' is provided in configuration."))
                     from name in ServiceName.From(nameString)
                     select name;

        return result.ThrowIfFail();
    }

    public static void ConfigureServiceUri(IHostApplicationBuilder builder)
    {
        ConfigureServiceName(builder);
        ConfigureServiceProviderUri(builder);

        builder.Services.TryAddSingleton(GetServiceUri);
    }

    private static ServiceUri GetServiceUri(IServiceProvider provider)
    {
        var serviceName = provider.GetRequiredService<ServiceName>();
        var serviceProviderUri = provider.GetRequiredService<ServiceProviderUri>();

        return ServiceUri.From(serviceProviderUri.ToUri()
                                                 .AppendPathSegment(serviceName)
                                                 .ToUri());
    }

    public static void ConfigureServiceProviderUri(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureAzureEnvironment(builder);
        AzureModule.ConfigureSubscriptionId(builder);
        AzureModule.ConfigureResourceGroupName(builder);

        builder.Services.TryAddSingleton(GetServiceProviderUri);
    }

    private static ServiceProviderUri GetServiceProviderUri(IServiceProvider provider)
    {
        var environment = provider.GetRequiredService<AzureEnvironment>();
        var subscriptionId = provider.GetRequiredService<SubscriptionId>();
        var resourceGroupName = provider.GetRequiredService<ResourceGroupName>();
        var configuration = provider.GetRequiredService<IConfiguration>();

        var apiVersion = configuration.GetValue("ARM_API_VERSION")
                                      .IfNone("2024-06-01-preview");

        return ServiceProviderUri.From(environment.ManagementEndpoint
                                                  .AppendPathSegment("subscriptions")
                                                  .AppendPathSegment(subscriptionId)
                                                  .AppendPathSegment("resourceGroups")
                                                  .AppendPathSegment(resourceGroupName)
                                                  .AppendPathSegment("providers/Microsoft.ApiManagement/service")
                                                  .SetQueryParam("api-version", apiVersion)
                                                  .ToUri());
    }

    public static void ConfigureServiceDirectory(IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(GetServiceDirectory);
    }

    private static ServiceDirectory GetServiceDirectory(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();

        var result = from path in configuration.GetValueOrFail("API_MANAGEMENT_SERVICE_OUTPUT_FOLDER_PATH")
                     from serviceDirectory in ServiceDirectory.From(path)
                     select serviceDirectory;

        return result.ThrowIfFail();
    }
}