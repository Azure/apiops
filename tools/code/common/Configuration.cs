using LanguageExt;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yaml2JsonNode;
using YamlDotNet.RepresentationModel;

namespace common;

public record ConfigurationJson
{
    public required JsonObject Value { get; init; }

    public static ConfigurationJson From(IConfiguration configuration) =>
        new()
        {
            Value = SerializeConfiguration(configuration) is JsonObject configurationJsonObject
                    ? configurationJsonObject
                    : new JsonObject(JsonNodeExtensions.Options)
        };

    private static JsonNode? SerializeConfiguration(IConfiguration configuration)
    {
        var jsonObject = new JsonObject(JsonNodeExtensions.Options);

        foreach (var child in configuration.GetChildren())
        {
            if (child.Path.EndsWith(":0", StringComparison.Ordinal))
            {
                var jsonArray = new JsonArray(JsonNodeExtensions.Options);

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
        [] => new JsonObject(JsonNodeExtensions.Options),
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
        var merged = new JsonObject();

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
}