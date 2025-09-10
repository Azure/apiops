using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using YamlDotNet.System.Text.Json;

namespace common;

public static class ConfigurationModule
{
    public static string YamlPath { get; } = "CONFIGURATION_YAML_PATH";

    private static readonly JsonNodeOptions nodeOptions = new() { PropertyNameCaseInsensitive = true };

    public static Option<string> GetValue(this IConfiguration configuration, string key)
    {
        var section = configuration.GetSection(key);

        if (section.Exists())
        {
            var value = section.Value;

            return string.IsNullOrEmpty(value)
                    ? Option.None
                    : value;
        }

        return Option.None;
    }

    public static string GetValueOrThrow(this IConfiguration configuration, string key) =>
        configuration.GetValue(key)
                     .IfNoneThrow(() => new InvalidOperationException($"Configuration key '{key}' not found or has an empty value."));

    // Configuration sources added last have higher priority. We empty existing sources,
    // add the new sources, and then add the existing sources back.
    public static IConfigurationBuilder AddWithLowestPriority(this IConfigurationBuilder builder, Func<IConfigurationBuilder, IConfigurationBuilder> adder)
    {
        ImmutableArray<IConfigurationSource> sources = [
            .. adder(new ConfigurationBuilder()).Sources,
            .. builder.Sources
            ];

        builder.Sources.Clear();
        sources.Iter(source => builder.Add(source), CancellationToken.None);

        return builder;
    }

    public static async ValueTask<JsonObject> GetJsonObject(IConfiguration configuration, CancellationToken cancellationToken)
    {
        var serializedConfiguration = SerializeConfiguration(configuration, cancellationToken);

        var yamlPathJsonOption = await GetJsonObjectFromYamlPath(configuration, cancellationToken);

        return serializedConfiguration switch
        {
            JsonObject jsonObject => yamlPathJsonOption.Map(yamlPathJson => jsonObject.MergeWith(yamlPathJson, mutateOriginal: true))
                                                       .IfNone(() => jsonObject),
            _ => new JsonObject(nodeOptions)
        };
    }

    private static async ValueTask<Option<JsonObject>> GetJsonObjectFromYamlPath(IConfiguration configuration, CancellationToken cancellationToken)
    {
        var fileOption = from path in configuration.GetValue(YamlPath)
                         select new FileInfo(path);

        var contentsOption = await fileOption.BindTask(async file => await file.ReadAsBinaryData(cancellationToken));

        return contentsOption.Map(contents =>
        {
            var contentsString = contents.ToString();
            return YamlConverter.Deserialize<JsonObject>(contentsString);
        });
    }

    private static JsonNode SerializeConfiguration(IConfiguration configuration, CancellationToken cancellationToken) =>
        ToJsonArray(configuration, cancellationToken).ToJsonNode()
            .IfNone(() => ToJsonValue(configuration).ToJsonNode())
            .IfNone(() => ToJsonObject(configuration, cancellationToken));

    private static Option<JsonArray> ToJsonArray(IConfiguration configuration, CancellationToken cancellationToken) =>
        // Configuration arrays should have numeric keys
        from childIndexes in configuration.GetChildren()
                                          .Traverse(child => int.TryParse(child.Key, out var index)
                                                                ? Option.Some((index, child))
                                                                : Option.None,
                                                    cancellationToken)
            // At least one child must exist
        where childIndexes.Length > 0

        let pairs = childIndexes.Unzip()
        let indexes = pairs.Item1
        let children = pairs.Item2
        // Numeric keys must be contiguous starting from 0
        where indexes.Order()
                     .SequenceEqual(Enumerable.Range(0, childIndexes.Length))
        select children.Select(section => SerializeConfiguration(section, cancellationToken))
                       .ToJsonArray();

    private static Option<JsonValue> ToJsonValue(IConfiguration configuration) =>
        configuration switch
        {
            IConfigurationSection section =>
                section.Value switch
                {
                    not null => JsonValue.Create(section.Value, nodeOptions),
                    _ => Option.None
                },
            _ => Option.None
        };

    private static JsonObject ToJsonObject(IConfiguration configuration, CancellationToken cancellationToken)
    {
        var children = configuration.GetChildren()
                                    .Select(child => KeyValuePair.Create(child.Key,
                                                                         (JsonNode?)SerializeConfiguration(child, cancellationToken)));

        return new JsonObject(children, nodeOptions);
    }
}

file static class OptionExtensions
{
    public static Option<JsonNode> ToJsonNode<T>(this Option<T> option) where T : JsonNode =>
        option.Select(t => (JsonNode)t);
}