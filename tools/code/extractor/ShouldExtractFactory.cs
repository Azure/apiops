using common;
using LanguageExt;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;

namespace extractor;

public delegate bool ShouldExtract<TName>(TName name) where TName : ResourceName;

public sealed class ShouldExtractFactory(ConfigurationJson configurationJson, ILoggerFactory loggerFactory)
{
    private readonly FrozenDictionary<string, FrozenSet<string>> configurationNames = GetConfigurationNames(configurationJson);
    private static readonly FrozenDictionary<Type, string> configurationSectionNames = GetConfigurationSectionNames();
    private readonly ILogger logger = loggerFactory.CreateLogger<ShouldExtractFactory>();

    private static FrozenDictionary<string, FrozenSet<string>> GetConfigurationNames(ConfigurationJson configurationJson) =>
        configurationJson.Value
                         // Get configuration sections that are JSON arrays
                         .ChooseValue(node => node.TryAsJsonArray())
                         // Map each JSON array to a set of strings
                         .MapValue(jsonArray => jsonArray.Choose(node => node.TryAsString())
                                                         .Where(value => string.IsNullOrWhiteSpace(value) is false)
                                                         .ToFrozenSet(StringComparer.OrdinalIgnoreCase))
                         .ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static FrozenDictionary<Type, string> GetConfigurationSectionNames() =>
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
        }
        .ToFrozenDictionary();

    public static string GetConfigurationSectionName<T>() =>
        configurationSectionNames.Find(typeof(T))
                                 .IfNone(() => throw new InvalidOperationException($"Resource type {typeof(T).Name} is not supported."));

    public static string GetNameToFind<T>(T name) where T : ResourceName =>
        configurationSectionNames.ContainsKey(typeof(T))
            ? name switch
            {
                ApiName apiName => ApiName.GetRootName(apiName).ToString(),
                _ => name.ToString()!
            }
            : throw new InvalidOperationException($"Resource type {typeof(T).Name} is not supported.");

    public ShouldExtract<TName> Create<TName>() where TName : ResourceName => ShouldExtract;

    private bool ShouldExtract<TName>(TName name) where TName : ResourceName
    {
        var sectionName = GetConfigurationSectionName<TName>();
        var nameToFind = GetNameToFind(name);

        var shouldExtract = configurationNames.Find(sectionName)
                                              .Map(set => set.Contains(nameToFind))
                                              .IfNone(true);

        if (shouldExtract is false)
        {
            logger.LogWarning("{ResourceType} {ResourceName} is not in configuration and will be skipped.", typeof(TName).Name, name);
        }

        return shouldExtract;
    }
}
