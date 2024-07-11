using common;
using System.Threading.Tasks;

namespace extractor;

public static class Program
{
    public static async Task Main(string[] arguments)
    {
        await HostingModule.RunHost(arguments, "extractor", AppModule.ConfigureRunApplication);
    }
}