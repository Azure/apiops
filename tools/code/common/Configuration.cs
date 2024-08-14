using LanguageExt;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yaml2JsonNode;
using YamlDotNet.RepresentationModel;

namespace common;

public record ConfigurationJson
{
    private static readonly JsonNodeOptions nodeOptions = new() { PropertyNameCaseInsensitive = true };

    public required JsonObject Value { get; init; }

    public static ConfigurationJson From(IConfiguration configuration) =>
        new()
        {
            Value = SerializeConfiguration(configuration) is JsonObject configurationJsonObject
                    ? configurationJsonObject
                    : new JsonObject(nodeOptions)
        };

    private static JsonNode? SerializeConfiguration(IConfiguration configuration)
    {
        var jsonObject = new JsonObject(nodeOptions);

        foreach (var child in configuration.GetChildren())
        {
            if (child.Path.EndsWith(":0", StringComparison.Ordinal))
            {
                var jsonArray = new JsonArray(nodeOptions);

                foreach (var arrayChild in configuration.GetChildren())
                {
                    jsonArray.Add(SerializeConfiguration(arrayChild));
                }

                return jsonArray;
            }
            else
            {
                jsonObject.Add(child.Key, SerializeConfiguration(child));
            }
        }

        if (jsonObject.Count == 0 && configuration is IConfigurationSection configurationSection)
        {
            string? sectionValue = configurationSection.Value;

            if (bool.TryParse(sectionValue, out var boolValue))
            {
                return JsonValue.Create(boolValue);
            }
            else if (decimal.TryParse(sectionValue, out var decimalValue))
            {
                return JsonValue.Create(decimalValue);
            }
            else if (long.TryParse(sectionValue, out var longValue))
            {
                return JsonValue.Create(longValue);
            }
            else
            {
                return JsonValue.Create(sectionValue);
            }
        }
        else
        {
            return jsonObject;
        }
    }

    public static ConfigurationJson FromYaml(TextReader textReader) =>
        new()
        {
            Value = YamlToJson(textReader)
        };

    private static JsonObject YamlToJson(TextReader reader)
    {
        var yamlStream = new YamlStream();
        yamlStream.Load(reader);

        return yamlStream.Documents switch
        {
        [] => new JsonObject(nodeOptions),
        [var document] => document.ToJsonNode()?.AsObject() ?? throw new JsonException("Failed to convert YAML to JSON."),
            _ => throw new JsonException("More than one YAML document was found.")
        };
    }

    public ConfigurationJson MergeWith(ConfigurationJson other) =>
        new()
        {
            Value = OverrideWith(Value, other.Value)
        };

    private static JsonObject OverrideWith(JsonObject current, JsonObject other)
    {
        var merged = new JsonObject(nodeOptions);

        foreach (var property in current)
        {
            string propertyName = property.Key;
            var currentPropertyValue = property.Value;

            if (other.TryGetPropertyValue(propertyName, out var otherPropertyValue))
            {
                if (currentPropertyValue is JsonObject currentObject && otherPropertyValue is JsonObject otherObject)
                {
                    merged[propertyName] = OverrideWith(currentObject, otherObject);
                }
                else
                {
                    merged[propertyName] = otherPropertyValue?.DeepClone();
                }
            }
            else
            {
                merged[propertyName] = currentPropertyValue?.DeepClone();
            }
        }

        foreach (var property in other)
        {
            string propertyName = property.Key;
            if (current.ContainsKey(propertyName) is false)
            {
                merged[propertyName] = property.Value?.DeepClone();
            }
        }

        return merged;
    }
}

public static class ConfigurationExtensions
{
    public static string GetValue(this IConfiguration configuration, string key) =>
        configuration.TryGetValue(key)
                     .IfNone(() => throw new KeyNotFoundException($"Could not find '{key}' in configuration."));

    public static Option<string> TryGetValue(this IConfiguration configuration, string key) =>
        configuration.TryGetSection(key)
                     .Where(section => section.Value is not null)
                     .Select(section => section.Value!);

    public static Option<IConfigurationSection> TryGetSection(this IConfiguration configuration, string key)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(key);

        return section.Exists()
                ? Option<IConfigurationSection>.Some(section)
                : Option<IConfigurationSection>.None;
    }

    public static IConfigurationBuilder AddUserSecretsWithLowestPriority(this IConfigurationBuilder builder, Assembly assembly, bool optional = true) =>
        builder.AddWithLowestPriority(b => b.AddUserSecrets(assembly, optional));

    private static IConfigurationBuilder AddWithLowestPriority(this IConfigurationBuilder builder, Func<IConfigurationBuilder, IConfigurationBuilder> adder)
    {
        // Configuration sources added last have the highest priority. We empty existing sources,
        // add the new sources, and then add the existing sources back.
        var adderSources = adder(new ConfigurationBuilder()).Sources;
        var existingSources = builder.Sources;
        var sources = adderSources.Concat(existingSources)
                                  .ToImmutableArray();

        builder.Sources.Clear();
        sources.Iter(source => builder.Add(source));

        return builder;
    }
}

public static class ConfigurationModule
{
    public static void ConfigureConfigurationJson(IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(GetConfigurationJson);
    }

    private static ConfigurationJson GetConfigurationJson(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();

        var configurationJson = ConfigurationJson.From(configuration);

        return TryGetConfigurationJsonFromYaml(configuration)
                .Map(configurationJson.MergeWith)
                .IfNone(configurationJson);
    }

    private static Option<ConfigurationJson> TryGetConfigurationJsonFromYaml(IConfiguration configuration) =>
        configuration.TryGetValue("CONFIGURATION_YAML_PATH")
                     .Map(path => new FileInfo(path))
                     .Where(file => file.Exists)
                     .Map(file =>
                     {
                         using var reader = File.OpenText(file.FullName);
                         return ConfigurationJson.FromYaml(reader);
                     });
}