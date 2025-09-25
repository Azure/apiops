using LanguageExt;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.Core;

namespace common;

public record ConfigurationValidationError(string PropertyPath, string Message)
{
    public override string ToString() => $"{PropertyPath}: {Message}";
}

public static class ConfigurationValidator
{
    private static readonly ImmutableHashSet<string> ValidExtractorSections = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "apiNames",
        "backendNames", 
        "diagnosticNames",
        "gatewayNames",
        "groupNames",
        "loggerNames",
        "namedValueNames",
        "policyFragmentNames",
        "productNames",
        "subscriptionNames",
        "tagNames",
        "versionSetNames",
        "workspaceNames"
    );

    public static Either<ImmutableList<ConfigurationValidationError>, ConfigurationJson> ValidateExtractorConfiguration(
        ConfigurationJson configurationJson, 
        ILogger? logger = null)
    {
        var errors = new List<ConfigurationValidationError>();

        // Validate root structure
        ValidateRootStructure(configurationJson.Value, errors);

        // Validate each known section
        ValidateKnownSections(configurationJson.Value, errors, logger);

        // Check for unknown sections
        ValidateUnknownSections(configurationJson.Value, errors, logger);

        return errors.Count == 0
            ? Either<ImmutableList<ConfigurationValidationError>, ConfigurationJson>.Right(configurationJson)
            : Either<ImmutableList<ConfigurationValidationError>, ConfigurationJson>.Left(errors.ToImmutableList());
    }

    public static Either<ImmutableList<ConfigurationValidationError>, ConfigurationJson> ValidateExtractorConfigurationFromFile(
        FileInfo configurationFile,
        ILogger? logger = null)
    {
        try
        {
            if (!configurationFile.Exists)
            {
                return Either<ImmutableList<ConfigurationValidationError>, ConfigurationJson>.Left(
                    ImmutableList.Create(new ConfigurationValidationError("file", $"Configuration file '{configurationFile.FullName}' does not exist.")));
            }

            using var reader = File.OpenText(configurationFile.FullName);
            var configurationJson = ConfigurationJson.FromYaml(reader);

            return ValidateExtractorConfiguration(configurationJson, logger);
        }
        catch (YamlException yamlEx)
        {
            return Either<ImmutableList<ConfigurationValidationError>, ConfigurationJson>.Left(
                ImmutableList.Create(new ConfigurationValidationError("yaml", $"YAML parsing error: {yamlEx.Message}")));
        }
        catch (JsonException jsonEx)
        {
            return Either<ImmutableList<ConfigurationValidationError>, ConfigurationJson>.Left(
                ImmutableList.Create(new ConfigurationValidationError("json", $"JSON conversion error: {jsonEx.Message}")));
        }
        catch (IOException ioEx)
        {
            return Either<ImmutableList<ConfigurationValidationError>, ConfigurationJson>.Left(
                ImmutableList.Create(new ConfigurationValidationError("file", $"File I/O error: {ioEx.Message}")));
        }
        catch (UnauthorizedAccessException authEx)
        {
            return Either<ImmutableList<ConfigurationValidationError>, ConfigurationJson>.Left(
                ImmutableList.Create(new ConfigurationValidationError("access", $"Access denied: {authEx.Message}")));
        }
    }

    private static void ValidateRootStructure(JsonObject rootObject, List<ConfigurationValidationError> errors)
    {
        if (rootObject.Count == 0)
        {
            errors.Add(new ConfigurationValidationError("root", "Configuration file is empty or contains no valid sections."));
            return;
        }

        // Check if all properties are arrays (as expected for extractor config)
        foreach (var property in rootObject)
        {
            if (property.Value is not JsonArray)
            {
                errors.Add(new ConfigurationValidationError(
                    property.Key,
                    $"Property '{property.Key}' must be an array of strings."));
            }
        }
    }

    private static void ValidateKnownSections(JsonObject rootObject, List<ConfigurationValidationError> errors, ILogger? logger)
    {
        foreach (var sectionName in ValidExtractorSections)
        {
            if (rootObject.TryGetPropertyValue(sectionName, out var sectionNode) && sectionNode is JsonArray sectionArray)
            {
                ValidateStringArray(sectionName, sectionArray, errors);
            }
        }
    }

    private static void ValidateStringArray(string sectionName, JsonArray array, List<ConfigurationValidationError> errors)
    {
        if (array.Count == 0)
        {
            errors.Add(new ConfigurationValidationError(sectionName, $"Section '{sectionName}' is empty. Consider removing it if no items need to be extracted."));
            return;
        }

        var duplicates = new System.Collections.Generic.HashSet<string>();
        var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < array.Count; i++)
        {
            var item = array[i];
            
            // Check if item is a string
            if (item is not JsonValue jsonValue || jsonValue.TryGetValue<string>(out var stringValue) == false)
            {
                errors.Add(new ConfigurationValidationError(
                    $"{sectionName}[{i}]",
                    "All items in the array must be strings."));
                continue;
            }

            // Check for empty or whitespace strings
            if (string.IsNullOrWhiteSpace(stringValue))
            {
                errors.Add(new ConfigurationValidationError(
                    $"{sectionName}[{i}]",
                    "Items cannot be empty or contain only whitespace."));
                continue;
            }

            // Check for duplicates
            if (!seen.Add(stringValue))
            {
                duplicates.Add(stringValue);
            }

            // Validate naming conventions
            ValidateNamingConvention(sectionName, i, stringValue, errors);
        }

        // Report duplicates
        foreach (var duplicate in duplicates)
        {
            errors.Add(new ConfigurationValidationError(
                sectionName,
                $"Duplicate item found: '{duplicate}'. Each item should be unique."));
        }
    }

    private static void ValidateNamingConvention(string sectionName, int index, string name, List<ConfigurationValidationError> errors)
    {
        // Basic naming convention validation
        if (name.Length > 256)
        {
            errors.Add(new ConfigurationValidationError(
                $"{sectionName}[{index}]",
                $"Name '{name}' is too long. Maximum length is 256 characters."));
        }

        // Check for invalid characters (basic validation)
        if (name.Contains("//", StringComparison.Ordinal) || name.Contains("\\\\", StringComparison.Ordinal))
        {
            errors.Add(new ConfigurationValidationError(
                $"{sectionName}[{index}]",
                $"Name '{name}' contains invalid character sequences."));
        }
    }

    private static void ValidateUnknownSections(JsonObject rootObject, List<ConfigurationValidationError> errors, ILogger? logger)
    {
        var unknownSections = rootObject
            .Where(kvp => !ValidExtractorSections.Contains(kvp.Key))
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var unknownSection in unknownSections)
        {
            var message = $"Unknown configuration section: '{unknownSection}'. Valid sections are: {string.Join(", ", ValidExtractorSections.OrderBy(s => s))}";
            
            logger?.LogWarning("Configuration validation warning: {Message}", message);
            
            // For now, treat unknown sections as warnings, not errors
            // Uncomment the next line if you want to treat them as errors
            // errors.Add(new ConfigurationValidationError(unknownSection, message));
        }
    }
}