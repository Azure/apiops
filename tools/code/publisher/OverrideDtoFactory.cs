using common;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace publisher;

public delegate TDto OverrideDto<TName, TDto>(TName name, TDto dto) where TName : notnull;

public class OverrideDtoFactory(ConfigurationJson configurationJson)
{
    private readonly FrozenDictionary<string, FrozenDictionary<string, JsonObject>> configurationDtos = GetConfigurationDtos(configurationJson);
    private static readonly FrozenDictionary<Type, string> sectionNames = GetConfigurationSectionNames();

    private static FrozenDictionary<string, FrozenDictionary<string, JsonObject>> GetConfigurationDtos(ConfigurationJson configurationJson) =>
        configurationJson.Value
                         // Get sections that are JSON arrays
                         .ChooseValue(node => node.TryAsJsonArray())
                         // Map sections to dictionaries
                         .MapValue(sectionArray => sectionArray.GetJsonObjects()
                                                               .Choose(jsonObject => from name in jsonObject.TryGetStringProperty("name").ToOption()
                                                                                     select (name, jsonObject))
                                                               .ToFrozenDictionary(StringComparer.OrdinalIgnoreCase))
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
        var currentJson = JsonObjectExtensions.Parse(current);
        var overridenJson = Override(currentJson, other);

        return overridenJson.Deserialize<T>() ?? throw new JsonException($"Failed to deserialize dto of type {typeof(T).Name}.");
    }

    private static JsonObject Override(JsonObject current, JsonObject other)
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
                    merged[propertyName] = Override(currentObject, otherObject);
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