using common;

namespace publisher.tests;

internal static class Common
{
    public static FileOperations NoOpFileOperations { get; } = new()
    {
        EnumerateServiceDirectoryFiles = () => [],
        GetSubDirectories = _ => Option.None,
        ReadFile = async (_, _) => Option.None
    };
}