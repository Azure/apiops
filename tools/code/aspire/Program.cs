using Aspire.Hosting;
using Projects;

internal static class Program
{
    private static void Main(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        //builder.AddProject<extractor>("extractor");
        builder.AddProject<publisher>("publisher");

        builder.Build().Run();
    }
}