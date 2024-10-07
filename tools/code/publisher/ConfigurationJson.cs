using common;
using LanguageExt;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using System;
using System.Linq;
using System.Text.Json.Nodes;

namespace publisher;

public delegate Option<JsonObject> FindConfigurationSection(params string[] sectionNames);

public static class ConfigurationJsonModule
{
    public static void ConfigureFindConfigurationSection(IHostApplicationBuilder builder)
    {
        ConfigurationModule.ConfigureConfigurationJson(builder);

        builder.Services.TryAddSingleton(GetFindConfigurationSection);
    }

    public static FindConfigurationSection GetFindConfigurationSection(IServiceProvider provider)
    {
        var configurationJson = provider.GetRequiredService<ConfigurationJson>();

        return sectionNames =>
            sectionNames.Select((sectionName, index) => (sectionName, index))
                        .Aggregate(Option<JsonObject>.None,
                                   (option, section) => section.index == 0
                                                        ? configurationJson.Value
                                                                           .TryGetJsonObjectProperty(section.sectionName)
                                                                           .ToOption()
                                                        : option.Bind(jsonObject => jsonObject.TryGetJsonObjectProperty(section.sectionName)
                                                                                              .ToOption()));
    }
}