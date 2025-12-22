using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;

namespace common.tests;

public static class ServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddTestActivitySource() =>
        services.AddSingleton<ActivitySource>(_ => new ActivitySource("tests"));

        public IServiceCollection AddNullLogger() =>
            services.AddSingleton<Microsoft.Extensions.Logging.ILogger>(NullLogger.Instance);
    }
}