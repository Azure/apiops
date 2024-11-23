using System.Collections.Immutable;

namespace codegen.resources;

public static class ApimResources
{
    public static ImmutableArray<IResource> All { get; } = [
        NamedValue.Instance,
        Api.Instance,
        ApiOperation.Instance,
        ApiOperationPolicy.Instance
        ];
}
