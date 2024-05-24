using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace integration.tests;

internal delegate ValueTask RunApplication(CancellationToken cancellationToken);

file sealed class RunApplicationHandler(ActivitySource activitySource, RunTests runTests)
{
    public async ValueTask Handle(CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(RunApplication));

        await runTests(cancellationToken);
    }
}

internal static class AppServices
{
    public static void ConfigureRunApplication(IServiceCollection services)
    {
        TestServices.ConfigureRunTests(services);

        services.TryAddSingleton<RunApplicationHandler>();
        services.TryAddSingleton<RunApplication>(provider => provider.GetRequiredService<RunApplicationHandler>().Handle);
    }
}