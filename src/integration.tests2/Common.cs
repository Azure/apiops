using common;
using CsCheck;

namespace integration.tests;

internal static class CommonModule
{
    public static Gen<string> GenerateDisplayName(ResourceName name) =>
        Gen.Const($"{name}-display");

    public static Gen<string> GenerateDisplayName(ResourceName name, string currentDisplayName) =>
        currentDisplayName.StartsWith($"name")
            ? Gen.Const(currentDisplayName.Split('-') switch
            {
                [.. var head, "display"] => $"{string.Join('-', head)}-display-2",
                [.. var head, "display", var last] when int.TryParse(last, out var number) => $"{string.Join('-', head)}-display-{number + 1}",
                _ => $"{currentDisplayName}-display"
            })
        : GenerateDisplayName(name);
}