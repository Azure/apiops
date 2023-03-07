using LanguageExt;
using Microsoft.Extensions.Configuration;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace publisher.integration.tests;

internal static class FileInfoExtensions
{
    public static string GetNameWithoutExtension(this FileInfo file)
    {
        return Path.GetFileNameWithoutExtension(file.FullName);
    }

    public static DirectoryInfo GetNonNullableDirectory(this FileInfo file)
    {
        return file.Directory ?? throw new InvalidOperationException($"File '{file.Name}' has a null directory.");
    }

    public static async ValueTask<string> ReadAsStringWithoutWhitespace(this FileInfo file, CancellationToken cancellationToken)
    {
        var stringValue = await file.ReadAsString(cancellationToken);
        var characters = stringValue.Where(character => char.IsWhiteSpace(character) is false);
        return string.Concat(characters);
    }

    public static async ValueTask<string> ReadAsString(this FileInfo file, CancellationToken cancellationToken)
    {
        return await File.ReadAllTextAsync(file.FullName, cancellationToken);
    }
}

internal static class OptionExtensions
{
    public static T IfNoneThrow<T>(this Option<T> option, string errorMessage)
    {
        return option.IfNone(() => throw new InvalidOperationException(errorMessage));
    }
}

internal static class IConfigurationExtensions
{
    public static string GetValue(this IConfiguration configuration, string key)
    {
        return configuration.TryGetValue(key)
                            .IfNoneThrow($"Could not find key '{key}' in configuration.");
    }

    public static Option<string> TryGetValue(this IConfiguration configuration, string key)
    {
        var section = configuration.GetSection(key);

        return section.Exists() ? section.Value : Option<string>.None;
    }
}