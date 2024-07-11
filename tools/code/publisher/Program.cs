using common;
using System.Threading.Tasks;

namespace publisher;

public static class Program
{
    public static async Task Main(string[] arguments)
    {
        await HostingModule.RunHost(arguments, "publisher", AppModule.ConfigureRunApplication);
    }
}