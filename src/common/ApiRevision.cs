using Azure.Core.Pipeline;
using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public static class ApiRevisionModule
{
    private const string separator = ";rev=";

    public static bool IsRootName(ResourceName name) =>
        Parse(name).IsNone;

    public static ResourceName GetRootName(ResourceName name) =>
        Parse(name).Map(x => x.RootName)
                   .IfNone(() => name);

    public static Option<(ResourceName RootName, int Revision)> Parse(ResourceName name) =>
        name.ToString().Split(separator).ToArray() switch
        {
            [var first, var second] => from rootName in ResourceName.From(first).ToOption()
                                       from revision in int.TryParse(second, out var revision)
                                                        ? Option.Some(revision)
                                                        : Option.None
                                       select (rootName, revision),
            _ => Option.None
        };

    public static ResourceName Combine(ResourceName rootName, int revision) =>
        revision < 1
            ? throw new InvalidOperationException($"Revision must be positive.")
            : ResourceName.From($"{rootName}{separator}{revision}")
                          .IfErrorThrow();
}