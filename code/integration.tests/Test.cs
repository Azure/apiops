using common;
using common.tests;
using CsCheck;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace integration.tests;

internal delegate ValueTask TestExtract(CancellationToken cancellationToken);

internal static class TestModule
{
    public static void ConfigureTestExtract(IHostApplicationBuilder builder)
    {
        ServiceModule.ConfigurePutService(builder);
        ExtractorModule.ConfigureRunExtractor(builder);
        ServiceModule.ConfigureDeleteService(builder);

        builder.Services.TryAddSingleton(GetTestExtract);
    }

    private static TestExtract GetTestExtract(IServiceProvider provider)
    {
        var putService = provider.GetRequiredService<PutService>();
        var runExtractor = provider.GetRequiredService<RunExtractor>();
        var deleteService = provider.GetRequiredService<DeleteService>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (CancellationToken cancellationToken) =>
        {
            using var _ = activitySource.StartActivity("test.extract");

            var generator = from name in GenerateServiceName()
                            from sku in GenerateServiceSku()
                            from model in ServiceModel.Generate()
                            let directoryPath = Path.Combine(Path.GetTempPath(), name.ToString())
                            let directory = ServiceDirectory.From(directoryPath).ThrowIfFail()
                            select (name, sku, model, directory);

            await generator.SampleAsync(async x =>
            {
                var (name, sku, model, directory) = x;

                await putService(name, sku, model, cancellationToken);
                await runExtractor(name, directory, cancellationToken);
                // await runExtractor(name, sku, model, cancellationToken);
                // await validateRun(name, sku, model, cancellationToken);
                await deleteService(name, cancellationToken);
            }, iter: 1);
        };
    }

    private static Gen<ServiceName> GenerateServiceName() =>
        from guid in Gen.Guid
        let name = $"{ServiceModule.TestServiceNamePrefix}{guid}"
        let validatedName = new string([.. name.Where(char.IsAsciiLetterOrDigit).Take(50)])
        select ServiceName.From(validatedName).ThrowIfFail();

    private static Gen<ServiceSku> GenerateServiceSku() =>
        Gen.OneOfConst<ServiceSku>(new ServiceSku.Basic(), new ServiceSku.Standard());
}