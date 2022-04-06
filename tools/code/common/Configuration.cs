using Microsoft.Extensions.Configuration;
using System;

namespace common;

public static class ConfigurationExtensions
{
    public static string? TryGetValue(this IConfiguration configuration, string key) =>
        configuration.TryGetSection(key)?.Value;

    public static string GetValue(this IConfiguration configuration, string key) =>
        configuration.TryGetValue(key) ?? throw new InvalidOperationException($"Could not find '{key}' in configuration.");

    public static IConfigurationSection? TryGetSection(this IConfiguration configuration, string key)
    {
        var section = configuration.GetSection(key);

        return section.Exists() ? section : null;
    }
}
