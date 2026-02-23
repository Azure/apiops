using common;
using common.tests;
using CsCheck;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace integration.tests;

internal delegate ImmutableArray<IResource> SortResources(IEnumerable<IResource> resources);

internal static class CommonModule
{
    public static void ConfigureSortResources(IHostApplicationBuilder builder)
    {
        ResourceGraphModule.ConfigureResourceGraph(builder);
        builder.TryAddSingleton(ResolveSortResources);
    }

    private static SortResources ResolveSortResources(IServiceProvider provider)
    {
        var graph = provider.GetRequiredService<ResourceGraph>();

        var resourceIndex = graph.TopologicallySortedResources
                                 .Select((resource, index) => (resource, index))
                                 .ToImmutableDictionary(x => x.resource, x => x.index);

        return resources =>
            [.. resources.OrderBy(resource => resourceIndex[resource])];
    }

    /// <summary>
    /// Generates an initial display name derived from the resource name (e.g. "myresource" => "myresource-display").
    /// </summary>
    public static Gen<string> GenerateDisplayName(ResourceName name) =>
        Gen.Const($"{name}-display");

    /// <summary>
    /// Generates an updated display name by incrementing a numeric suffix
    /// (e.g. "myresource-display" => "myresource-display-2" => "myresource-display-3").
    /// Falls back to generating a fresh display name if the current one doesn't match the expected pattern.
    /// </summary>
    public static Gen<string> GenerateDisplayName(ResourceName name, string currentDisplayName) =>
        currentDisplayName.StartsWith($"{name}")
            ? Gen.Const(currentDisplayName.Split('-') switch
            {
                [.. var head, "display"] => $"{string.Join('-', head)}-display-2",
                [.. var head, "display", var last] when int.TryParse(last, out var number) => $"{string.Join('-', head)}-display-{number + 1}",
                _ => $"{currentDisplayName}-display"
            })
        : GenerateDisplayName(name);

    /// <summary>
    /// Generates an initial description derived from the resource name (e.g. "myresource" => "myresource-description").
    /// </summary>
    public static Gen<string> GenerateDescription(ResourceName name) =>
        Gen.Const($"{name}-description");

    /// <summary>
    /// Generates an updated description by incrementing a numeric suffix
    /// (e.g. "myresource-description" => "myresource-description-2" => "myresource-description-3").
    /// Falls back to generating a fresh description if the current one doesn't match the expected pattern.
    /// </summary>
    public static Gen<string> GenerateDescription(ResourceName name, string currentDescription) =>
        currentDescription.StartsWith($"{name}")
            ? Gen.Const(currentDescription.Split('-') switch
            {
                [.. var head, "description"] => $"{string.Join('-', head)}-description-2",
                [.. var head, "description", var last] when int.TryParse(last, out var number) => $"{string.Join('-', head)}-description-{number + 1}",
                _ => $"{currentDescription}-description"
            })
        : GenerateDescription(name);
}

internal static class PolicyModule
{
    public static ResourceName ResourceName { get; } =
        ResourceName.From("policy").IfErrorThrow();

    public static Gen<string> GenerateSetVariableSnippet(IEnumerable<NamedValueModel> namedValues) =>
        from variableName in Generator.AlphanumericWord
        from value in GenerateWordOrNamedValue(namedValues)
        select $"""<set-variable name="{variableName}" value="{value}" />""";

    private static Gen<string> GenerateWordOrNamedValue(IEnumerable<NamedValueModel> namedValues) =>
        Gen.OneOf([.. namedValues
                         .Where(model => model.Secret is false)
                         .Select(model => Gen.Const($$$"""{{{{{model.DisplayName}}}}}"""))
                         .Append(Generator.AlphanumericWord)]);

    public static Gen<string> GenerateInboundSnippet(IEnumerable<NamedValueModel> namedValues, IEnumerable<PolicyFragmentModel> fragments) =>
        from snippetGens in Generator.SubSetOf([GenerateSetVariableSnippet(namedValues),
                                                GenerateIpFilterSnippet(),
                                                GenerateFindAndReplaceSnippet(namedValues),
                                                GenerateSetHeaderSnippet(namedValues),
                                                GenerateIncludeFragmentSnippet(fragments)])
        from snippets in Generator.Traverse(snippetGens, gen => gen)
        select $"""
                <inbound>
                    {string.Join(Environment.NewLine, snippets)}
                </inbound>
                """;

    private static Gen<string> GenerateIpFilterSnippet() =>
        from last3 in Gen.Int[0, 255].Array[3]
        let ips = last3.Prepend(10)
        let address = string.Join('.', ips)
        select $"""
                <ip-filter action="allow">
                    <address>{address}</address>
                </ip-filter>
                """;

    private static Gen<string> GenerateFindAndReplaceSnippet(IEnumerable<NamedValueModel> namedValues) =>
        from @from in GenerateWordOrNamedValue(namedValues)
        from to in GenerateWordOrNamedValue(namedValues)
        select $"""<find-and-replace from="{@from}" to="{to}" />""";

    private static Gen<string> GenerateSetHeaderSnippet(IEnumerable<NamedValueModel> namedValues) =>
        from x in GenerateHeader(namedValues)
        select $"""
                <set-header name="{x.Name}" exists-action="append">
                    <value>{x.Value}</value>
                </set-header>
                """;

    private static Gen<(string Name, string Value)> GenerateHeader(IEnumerable<NamedValueModel> namedValues) =>
        Gen.OneOf(from contentType in Gen.OneOfConst("application/json", "application/xml", "text/plain")
                  select ("Content-Type", contentType),
                  from customValue in GenerateWordOrNamedValue(namedValues)
                  select ("X-Custom-Header", customValue));

    private static Gen<string> GenerateIncludeFragmentSnippet(IEnumerable<PolicyFragmentModel> fragments) =>
        from includes in Generator.SubSetOf([.. from fragment in fragments
                                                let name = fragment.Key.Name
                                                select $"""<include-fragment fragment-id="{name}" />"""])
        select string.Join(Environment.NewLine, includes);

    public static Gen<string> GenerateOutboundSnippet(IEnumerable<NamedValueModel> namedValues, IEnumerable<PolicyFragmentModel> fragments) =>
        from snippetGens in Generator.SubSetOf([GenerateSetVariableSnippet(namedValues),
                                                GenerateFindAndReplaceSnippet(namedValues),
                                                GenerateSetHeaderSnippet(namedValues),
                                                GenerateIncludeFragmentSnippet(fragments)])
        from snippets in Generator.Traverse(snippetGens, gen => gen)
        select $"""
                <outbound>
                    {string.Join(Environment.NewLine, snippets)}
                </outbound>
                """;

    /// <summary>
    /// Compares two policy XML strings by stripping all whitespace and comparing case-insensitively.
    /// Round-tripping through APIM/extractor may change whitespace and formatting.
    /// </summary>
    public static bool FuzzyEquals(string? first, string? second)
    {
        var normalizedFirst = new string([.. first?.Where(c => !char.IsWhiteSpace(c)) ?? []]);
        var normalizedSecond = new string([.. second?.Where(c => !char.IsWhiteSpace(c)) ?? []]);
        return normalizedFirst.Equals(normalizedSecond, StringComparison.OrdinalIgnoreCase);
    }
}