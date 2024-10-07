using common;
using LanguageExt;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace publisher;

public delegate TDto OverrideDto<TName, TDto>(TName name, TDto dto) where TName : notnull;

public sealed class OverrideDtoFactory(ConfigurationJson configurationJson)
{
    private readonly FrozenDictionary<string, FrozenDictionary<string, JsonObject>> configurationDtos = GetConfigurationDtos(configurationJson);
    private static readonly FrozenDictionary<Type, string> sectionNames = GetConfigurationSectionNames();

    private static FrozenDictionary<string, FrozenDictionary<string, JsonObject>> GetConfigurationDtos(ConfigurationJson configurationJson) =>
        configurationJson.Value
                         // Get sections that are JSON arrays
                         .ChooseValues(node => node.TryAsJsonArray().ToOption())
                         // Convert each JSON array a dictionary
                         .Select(kvp => kvp.MapValue(jsonArray => jsonArray.PickJsonObjects()
                                                                           // Where the JSON object has a "name" property
                                                                           .Choose(jsonObject => from name in jsonObject.TryGetStringProperty("name").ToOption()
                                                                                                 select (name, jsonObject))
                                                                           // Return a dictionary where the key is the "name" property and the value is the JSON object                               
                                                                           .ToFrozenDictionary(StringComparer.OrdinalIgnoreCase)))
                         // Return a dictionary where the key is the section name and the value is the dictionary of JSON objects.
                         // For instance, one item in this dictionary could be "apis" -> { "api1": { ... }, "api2": { ... } }
                         .ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static FrozenDictionary<Type, string> GetConfigurationSectionNames() =>
        new Dictionary<Type, string>
        {
            [typeof(NamedValueName)] = "namedValues",
            [typeof(TagName)] = "tags",
            [typeof(GatewayName)] = "gateways",
            [typeof(VersionSetName)] = "versionSets",
            [typeof(BackendName)] = "backends",
            [typeof(LoggerName)] = "loggers",
            [typeof(DiagnosticName)] = "diagnostics",
            [typeof(PolicyFragmentName)] = "policyFragments",
            [typeof(ServicePolicyName)] = "servicePolicies",
            [typeof(ProductName)] = "products",
            [typeof(GroupName)] = "groups",
            [typeof(SubscriptionName)] = "subscriptions",
            [typeof(ApiName)] = "apis",
        }
        .ToFrozenDictionary();

    public static string GetSectionName<T>() =>
        sectionNames.Find(typeof(T))
                    .IfNone(() => throw new InvalidOperationException($"Resource type {typeof(T).Name} is not supported."));

    public static string GetNameToFind<T>(T name) where T : notnull =>
        sectionNames.ContainsKey(typeof(T))
            ? name switch
            {
                ApiName apiName => ApiName.GetRootName(apiName).ToString(),
                _ => name.ToString()!
            }
            : throw new InvalidOperationException($"Resource type {typeof(T).Name} is not supported.");

    public OverrideDto<TName, TDto> Create<TName, TDto>() where TName : notnull => Override;

    private TDto Override<TName, TDto>(TName name, TDto dto) where TName : notnull
    {
        var sectionName = GetSectionName<TName>();
        var nameToFind = GetNameToFind(name);

        return configurationDtos.Find(sectionName)
                                .Bind(section => section.Find(nameToFind))
                                .Map(overrideJson => Override(dto, overrideJson))
                                .IfNone(dto);
    }

    public static T Override<T>(T current, JsonObject other)
    {
        var currentJson = BinaryData.FromObjectAsJson(current)
                                    .ToObjectFromJson<JsonObject>();
        var overridenJson = Override(currentJson, other);

        return overridenJson.Deserialize<T>() ?? throw new JsonException($"Failed to deserialize dto of type {typeof(T).Name}.");
    }

    private static JsonObject Override(JsonObject current, JsonObject other)
    {
        var nodeOptions = new JsonNodeOptions { PropertyNameCaseInsensitive = true };
        var merged = new JsonObject(nodeOptions);

        foreach (var property in current)
        {
            string propertyName = property.Key;
            var currentPropertyValue = property.Value;

            merged[propertyName] = other.TryGetPropertyValue(propertyName, out var otherPropertyValue)
                                    ? (currentPropertyValue, otherPropertyValue) switch
                                    {
                                        (JsonObject currentObject, JsonObject otherObject) => Override(currentObject, otherObject),
                                        _ => otherPropertyValue?.DeepClone(),
                                    }
                                    : (currentPropertyValue?.DeepClone());
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

internal static class OverrideDtoModule
{
    public static void ConfigureOverrideDtoFactory(IHostApplicationBuilder builder)
    {
        ConfigurationModule.ConfigureConfigurationJson(builder);

        builder.Services.TryAddSingleton(GetOverrideDtoFactory);
    }

    private static OverrideDtoFactory GetOverrideDtoFactory(IServiceProvider provider)
    {
        var configurationJson = provider.GetRequiredService<ConfigurationJson>();

        return new OverrideDtoFactory(configurationJson);
    }
}