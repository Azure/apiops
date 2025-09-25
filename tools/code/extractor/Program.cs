using common;
using System.Threading.Tasks;

namespace extractor;

public static class Program
{
    public static async Task<int> Main(string[] arguments)
    {
        // Check if this is a configuration validation command
        var validationResult = ConfigurationValidationCommand.ValidateConfigurationFile(arguments);
        if (validationResult >= 0)
        {
            return validationResult;
        }

        // Otherwise, run the normal extractor
        await HostingModule.RunHost(arguments, "extractor", AppModule.ConfigureRunApplication);
        return 0;
    }
}