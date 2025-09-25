using common;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;

namespace extractor;

public static class ConfigurationValidationCommand
{
    public static int ValidateConfigurationFile(string[] args, ILogger? logger = null)
    {
        if (args.Length == 0 || args[0] != "validate-config")
        {
            return -1; // Not a validation command
        }

        if (args.Length < 2)
        {
            Console.WriteLine("Usage: extractor validate-config <path-to-config-file>");
            Console.WriteLine("Example: extractor validate-config configuration.extractor.yaml");
            return 1;
        }

        var configFilePath = args[1];
        var configFile = new FileInfo(configFilePath);

        Console.WriteLine($"Validating configuration file: {configFile.FullName}");

        var result = ConfigurationValidator.ValidateExtractorConfigurationFromFile(configFile, logger);

        return result.Match(
            errors =>
            {
                Console.WriteLine("❌ Configuration validation FAILED:");
                Console.WriteLine();

                foreach (var error in errors.OrderBy(e => e.PropertyPath))
                {
                    Console.WriteLine($"  • {error}");
                }

                Console.WriteLine();
                Console.WriteLine($"Found {errors.Count} validation error(s). Please fix them and try again.");
                return 1;
            },
            _ =>
            {
                Console.WriteLine("✅ Configuration validation PASSED!");
                Console.WriteLine("The configuration file is valid and ready to use.");
                return 0;
            }
        );
    }
}