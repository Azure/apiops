using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using System.Threading.Tasks;

namespace publisher.integration.tests;

[SetUpFixture]
public class Fixture
{
    private static readonly IConfiguration configuration = GetConfiguration();

    public static ServiceDirectory ExtractorServiceDirectory { get; } = GetExtractorServiceDirectory();
    public static ServiceDirectory PublisherServiceDirectory { get; } = GetPublisherServiceDirectory();

    [OneTimeSetUp]
    public async Task DoSetup()
    {
        await TestContext.Progress.WriteLineAsync("Simulating setup...");
        await Task.Delay(5000);
        await TestContext.Progress.WriteLineAsync("Setup complete");
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        await TestContext.Progress.WriteLineAsync("Simulating teardown...");
        await Task.Delay(5000);
        await TestContext.Progress.WriteLineAsync("Teardown complete");
    }

    private static IConfiguration GetConfiguration()
    {
        return new ConfigurationBuilder().AddEnvironmentVariables()
                                         .Build();
    }

    private static ServiceDirectory GetExtractorServiceDirectory()
    {
        var path = configuration["EXTRACTOR_ARTIFACTS_PATH"];
        return new(new(new(path)));
    }

    private static ServiceDirectory GetPublisherServiceDirectory()
    {
        var path = configuration["PUBLISHER_ARTIFACTS_PATH"];
        return new(new(new(path)));
    }
}