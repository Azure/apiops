using common;
using LanguageExt;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace extractor;

public delegate Option<FrozenSet<TName>> FindConfigurationNames<TName>() where TName : ResourceName;

public sealed class FindConfigurationNamesFactory(ConfigurationJson configurationJson)
{
    private static readonly FrozenDictionary<Type, string> typeSectionNames = GetTypeSectionNames();
    private static readonly FrozenDictionary<string, Type> sectionNameTypes = GetSectionNameTypes(typeSectionNames);
    private readonly FrozenDictionary<Type, FrozenSet<string>> namesToExtract = GetNamesToExtract(configurationJson);

    private static FrozenDictionary<Type, string> GetTypeSectionNames() =>
        new Dictionary<Type, string>
        {
            [typeof(NamedValueName)] = "namedValueNames",
            [typeof(TagName)] = "tagNames",
            [typeof(GatewayName)] = "gatewayNames",
            [typeof(VersionSetName)] = "versionSetNames",
            [typeof(BackendName)] = "backendNames",
            [typeof(LoggerName)] = "loggerNames",
            [typeof(DiagnosticName)] = "diagnosticNames",
            [typeof(PolicyFragmentName)] = "policyFragmentNames",
            [typeof(ProductName)] = "productNames",
            [typeof(GroupName)] = "groupNames",
            [typeof(SubscriptionName)] = "subscriptionNames",
            [typeof(ApiName)] = "apiNames",
            [typeof(WorkspaceName)] = "workspaceNames",
        }
        .ToFrozenDictionary();

    private static FrozenDictionary<string, Type> GetSectionNameTypes(FrozenDictionary<Type, string> typeSectionNames) =>
        typeSectionNames.ToFrozenDictionary(kvp => kvp.Value, kvp => kvp.Key, StringComparer.OrdinalIgnoreCase);

    private static FrozenDictionary<Type, FrozenSet<string>> GetNamesToExtract(ConfigurationJson configurationJson) =>
        configurationJson.Value
                         // Get configuration sections that are JSON arrays
                         .ChooseValues(node => node.TryAsJsonArray().ToOption())
                         // Map each JSON array to a set of strings
                         .Select(kvp => kvp.MapValue(jsonArray => jsonArray.PickStrings()
                                                                           .Where(value => string.IsNullOrWhiteSpace(value) is false)
                                                                           .ToFrozenSet(StringComparer.OrdinalIgnoreCase)))
                         // Map each configuration section to a resource name type
                         .ChooseKeys(sectionNameTypes.Find)
                         .ToFrozenDictionary();

    public static string GetConfigurationSectionName<T>() =>
        typeSectionNames.Find(typeof(T))
                        .IfNone(() => throw new InvalidOperationException($"Resource type {typeof(T).Name} is not supported."));

    public static string GetNameToFind<T>(T name) where T : ResourceName =>
        typeSectionNames.ContainsKey(typeof(T))
            ? name switch
            {
                ApiName apiName => ApiName.GetRootName(apiName).Value,
                _ => name.Value
            }
            : throw new InvalidOperationException($"Resource type {typeof(T).Name} is not supported.");

    public FindConfigurationNames<TName> Create<TName>() where TName : ResourceName, IResourceName<TName> =>
        () => namesToExtract.Find(typeof(TName))
                            .Map(set => set.Select(TName.From)
                                           .ToFrozenSet());
}

internal static class ConfigurationModule
{
    public static void ConfigureFindConfigurationNamesFactory(IHostApplicationBuilder builder)
    {
        common.ConfigurationModule.ConfigureConfigurationJson(builder);

        builder.Services.TryAddSingleton(GetFindConfigurationNamesFactory);
    }

    private static FindConfigurationNamesFactory GetFindConfigurationNamesFactory(IServiceProvider provider)
    {
        var configurationJson = provider.GetRequiredService<ConfigurationJson>();
        var logger = provider.GetRequiredService<ILogger<FindConfigurationNamesFactory>>();

        // Perform additional validation specific to extractor configuration
        ValidateExtractorSpecificConfiguration(configurationJson, logger);

        return new FindConfigurationNamesFactory(configurationJson);
    }

    private static void ValidateExtractorSpecificConfiguration(ConfigurationJson configurationJson, ILogger logger)
    {
        // Additional validation logic specific to extractor can go here
        var rootObject = configurationJson.Value;
        
        // Log configuration summary
        var configuredSections = rootObject
            .Where(kvp => kvp.Value is JsonArray array && array.Count > 0)
            .Select(kvp => $"{kvp.Key} ({((JsonArray)kvp.Value!).Count} items)")
            .ToList();
            
        if (configuredSections.Any())
        {
            logger.LogInformation("Extractor configured to extract: {ConfiguredSections}", 
                string.Join(", ", configuredSections));
        }
        else
        {
            logger.LogWarning("No extraction configuration found. Extractor will extract all resources.");
        }

        // Validate cross-section dependencies (example: if APIs are specified, ensure related resources are considered)
        ValidateCrossSectionDependencies(rootObject, logger);
    }

    private static void ValidateCrossSectionDependencies(System.Text.Json.Nodes.JsonObject rootObject, ILogger logger)
    {
        var hasApis = rootObject.ContainsKey("apiNames") && 
                     rootObject["apiNames"] is JsonArray apiArray && apiArray.Count > 0;
        
        var hasProducts = rootObject.ContainsKey("productNames") && 
                         rootObject["productNames"] is JsonArray productArray && productArray.Count > 0;
        
        if (hasApis && !hasProducts)
        {
            logger.LogInformation("APIs are configured for extraction but no products specified. " +
                                "Consider if related products should also be extracted.");
        }

        if (hasProducts && !hasApis)
        {
            logger.LogInformation("Products are configured for extraction but no APIs specified. " +
                                "Consider if related APIs should also be extracted.");
        }
    }
}