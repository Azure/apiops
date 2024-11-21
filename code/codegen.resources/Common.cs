using System.Collections.Immutable;

namespace codegen.resources;

public static class ApimResources
{
    public static ImmutableArray<IResource> All { get; } = [
        new NamedValue()
        ];
}
