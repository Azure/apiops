namespace creator;

public static class Program
{
    public static async Task Main(string[] arguments)
    {
        await CreateBuilder(arguments).Build().RunAsync();
    }

    private static IHostBuilder CreateBuilder(string[] arguments)
    {
        return Host
            .CreateDefaultBuilder(arguments)
            .ConfigureAppConfiguration(ConfigureConfiguration)
            .ConfigureServices(ConfigureServices);
    }

    private static void ConfigureConfiguration(IConfigurationBuilder builder)
    {
        var config = new Dictionary<string, string>();

        builder.AddInMemoryCollection(config);
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddTransient(GetTokenCredential)
                .AddSingleton(GetArmClient)
                .AddHostedService<Creator>();
    }

    private static TokenCredential GetTokenCredential(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();
        var configurationSection = configuration.GetSection("AZURE_BEARER_TOKEN");

        return configurationSection.Exists()
            ? new StaticTokenCredential(configurationSection.Value)
            : new DefaultAzureCredential();
    }

    private static ArmClient GetArmClient(IServiceProvider provider)
    {
        var tokenCredential = provider.GetRequiredService<TokenCredential>();

        return new ArmClient(tokenCredential);
    }
}
