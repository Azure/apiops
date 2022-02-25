namespace extractor;

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
                .ConfigureHttp()
                .AddHostedService<Extractor>();
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

    private static IServiceCollection ConfigureHttp(this IServiceCollection services)
    {
        var getRetryDuration = (int retryCount) =>
            Backoff.DecorrelatedJitterBackoffV2(TimeSpan.FromMilliseconds(500), retryCount, fastFirst: true)
                   .Last();

        var retryOnTimeoutPolicy =
            HttpPolicyExtensions.HandleTransientHttpError()
                                .OrResult(response => response.StatusCode is HttpStatusCode.TooManyRequests)
                                .WaitAndRetryAsync(10, getRetryDuration);

        services.AddHttpClient<NonAuthenticatedHttpClient>()
                .AddPolicyHandler(retryOnTimeoutPolicy);

        return services;
    }
}
