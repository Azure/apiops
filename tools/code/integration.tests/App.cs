using common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using System;
using System.Diagnostics;

namespace integration.tests;

internal static class AppModule
{
    public static void ConfigureRunApplication(IHostApplicationBuilder builder)
    {
        TestModule.ConfigureTestExtractor(builder);
        TestModule.ConfigureTestExtractThenPublish(builder);
        TestModule.ConfigureTestPublisher(builder);
        TestModule.ConfigureCleanUpTests(builder);
        //TestModule.ConfigureTestWorkspaces(builder);

        builder.Services.TryAddSingleton(GetRunApplication);
    }

    private static RunApplication GetRunApplication(IServiceProvider provider)
    {
        var testExtractor = provider.GetRequiredService<TestExtractor>();
        var testExtractThenPublish = provider.GetRequiredService<TestExtractThenPublish>();
        var testPublisher = provider.GetRequiredService<TestPublisher>();
        //var testWorkspaces = provider.GetRequiredService<TestWorkspaces>();
        var cleanUpTests = provider.GetRequiredService<CleanUpTests>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(RunApplication));

            await testExtractor(cancellationToken);
            await testExtractThenPublish(cancellationToken);
            await testPublisher(cancellationToken);
            await cleanUpTests(cancellationToken);
            //await testWorkspaces(cancellationToken);
        };
    }
}