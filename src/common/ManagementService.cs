using Flurl;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.IO;

namespace common;

public sealed record ServiceUri
{
    private readonly Uri value;

    private ServiceUri(Uri value)
    {
        this.value = value;
    }

    public static Result<ServiceUri> From(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
            ? new ServiceUri(uri)
            : Error.From($"'{value}' is not a valid URI.");

    public override string ToString() => value.ToString();

    public override int GetHashCode() => value.GetHashCode();

    public Uri ToUri() =>
        value;
}

public sealed record ServiceDirectory
{
    private readonly DirectoryInfo value;

    private ServiceDirectory(DirectoryInfo value)
    {
        this.value = value;
    }

    public static ServiceDirectory FromDirectoryInfo(DirectoryInfo value) =>
        new(value);

    public static ServiceDirectory FromPath(string path) =>
        new(new DirectoryInfo(path));

    public DirectoryInfo ToDirectoryInfo() =>
        value;

    public override string ToString() => value.FullName;
}

public sealed record ServiceName
{
    private readonly string value;

    private ServiceName(string value)
    {
        this.value = value;
    }

    public static Result<ServiceName> From(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? Error.From($"Service name '{value}' cannot be empty or whitespace.")
            : new ServiceName(value);

    public override string ToString() => value;

    public override int GetHashCode() =>
        StringComparer.OrdinalIgnoreCase.GetHashCode(value);

    public bool Equals(ServiceName? other) =>
        other is not null &&
            value.Equals(other.value, StringComparison.OrdinalIgnoreCase);
}

public static class ManagementServiceModule
{
    public static void ConfigureServiceUri(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureServiceProviderUri(builder);
        ConfigureServiceName(builder);

        builder.TryAddSingleton(ResolveServiceUri);
    }

    private static ServiceUri ResolveServiceUri(IServiceProvider provider)
    {
        var serviceProviderUri = provider.GetRequiredService<ServiceProviderUri>();
        var serviceName = provider.GetRequiredService<ServiceName>();

        var uri = serviceProviderUri.ToUri()
                                    .AppendPathSegment(serviceName)
                                    .ToString();

        return ServiceUri.From(uri).IfErrorThrow();
    }

    public static void ConfigureServiceDirectory(IHostApplicationBuilder builder) =>
        builder.TryAddSingleton(ResolveServiceDirectory);

    private static ServiceDirectory ResolveServiceDirectory(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();

        var path = configuration.GetValueOrThrow("API_MANAGEMENT_SERVICE_OUTPUT_FOLDER_PATH");

        return ServiceDirectory.FromPath(path);
    }

    public static void ConfigureServiceName(IHostApplicationBuilder builder) =>
        builder.TryAddSingleton(ResolveServiceName);

    private static ServiceName ResolveServiceName(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();

        var name = configuration.GetValue("API_MANAGEMENT_SERVICE_NAME")
                                .IfNone(() => configuration.GetValueOrThrow("apimServiceName"));

        return ServiceName.From(name).IfErrorThrow();
    }
}