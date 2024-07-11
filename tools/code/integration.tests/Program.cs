using common;
using System.Threading.Tasks;

namespace integration.tests;

internal static class Program
{
    public static async Task Main(string[] arguments)
    {
        await HostingModule.RunHost(arguments, "integration.tests", AppModule.ConfigureRunApplication);
    }
}