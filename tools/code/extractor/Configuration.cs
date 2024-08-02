using common;
using LanguageExt;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;

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

        return new FindConfigurationNamesFactory(configurationJson);
    }
}