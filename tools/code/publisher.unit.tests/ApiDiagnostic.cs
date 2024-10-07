using common;
using common.tests;
using CsCheck;
using LanguageExt;
using LanguageExt.UnsafeValueAccess;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace publisher.unit.tests;

public class FindApiDiagnosticDtoTests
{
    [Fact]
    public async Task Returns_none_if_the_dto_does_not_exist()
    {
        var generator = from fixture in Fixture.Generate()
                        where fixture.OriginalDto.IsNone
                        select fixture;

        await generator.SampleAsync(async fixture =>
        {
            var dtoOption = await fixture.Run(CancellationToken.None);

            dtoOption.Should().BeNone();
        });
    }

    [Fact]
    public async Task Returns_the_original_dto_if_there_is_no_override()
    {
        var generator = from fixture in Fixture.Generate()
                        where fixture.OriginalDto.IsSome
                        where fixture.DtoOverride.IsNone
                        select fixture;

        await generator.SampleAsync(async fixture =>
        {
            var dtoOption = await fixture.Run(CancellationToken.None);

            var expectedDto = fixture.OriginalDto.ValueUnsafe() ?? throw new InvalidOperationException("Expected dto should not be null.");
            dtoOption.Should().BeSome(expectedDto);
        });
    }

    [Fact]
    public async Task Returns_the_overridden_dto_if_there_is_an_override()
    {
        var generator = from fixture in Fixture.Generate()
                        where fixture.OriginalDto.IsSome
                        where fixture.DtoOverride.IsSome
                        select fixture;

        await generator.SampleAsync(async fixture =>
        {
            var dtoOption = await fixture.Run(CancellationToken.None);

            // Assert
            var originalDto = fixture.OriginalDto.ValueUnsafe() ?? throw new InvalidOperationException("Original dto should not be null.");
            var dtoOverride = fixture.DtoOverride.ValueUnsafe() ?? throw new InvalidOperationException("Override should not be null.");
            var expectedDto = OverrideDtoFactory.Override(originalDto, dtoOverride);
            dtoOption.Should().BeSome(expectedDto);
        });
    }

    private sealed record Fixture
    {
        public required ManagementServiceDirectory ServiceDirectory { get; init; }
        public required ApiName ApiName { get; init; }
        public required ApiDiagnosticName Name { get; init; }
        public required Option<ApiDiagnosticDto> OriginalDto { get; init; }
        public required Option<JsonObject> DtoOverride { get; init; }

        public async ValueTask<Option<ApiDiagnosticDto>> Run(CancellationToken cancellationToken)
        {
            var provider = GetServiceProvider();

            var findDto = ApiDiagnosticModule.GetFindApiDiagnosticDto(provider);

            return await findDto(Name, ApiName, cancellationToken);
        }

        private IServiceProvider GetServiceProvider()
        {
            var services = new ServiceCollection();

            services.AddSingleton(ServiceDirectory);

            services.AddSingleton<TryGetFileContents>(async (file, cancellationToken) =>
            {
                await ValueTask.CompletedTask;

                return OriginalDto.Map(dto => BinaryData.FromObjectAsJson(dto));
            });

            services.AddSingleton(new ConfigurationJson
            {
                Value = DtoOverride.Map(@override => new JsonObject
                {
                    ["apis"] = new JsonObject
                    {
                        [ApiName.Value] = new JsonObject
                        {
                            ["diagnostics"] = new JsonObject
                            {
                                [Name.Value] = @override
                            }
                        }
                    }
                }).IfNone([])
            });

            services.AddSingleton(ConfigurationJsonModule.GetFindConfigurationSection);

            return services.BuildServiceProvider();
        }

        public static Gen<Fixture> Generate() =>
            from serviceDirectory in from directoryInfo in Generator.DirectoryInfo
                                     select ManagementServiceDirectory.From(directoryInfo)
            from apiName in from apiType in ApiType.Generate()
                            from apiName in ApiModel.GenerateName(apiType)
                            select apiName
            from name in ApiDiagnosticModel.GenerateName()
            from originalDto in from modelOption in ApiDiagnosticModel.Generate().OptionOf()
                                select modelOption.Map(ModelToDto)
            from dtoOverride in from modelOption in ApiDiagnosticModel.Generate().OptionOf()
                                select from model in modelOption
                                       let dto = ModelToDto(model)
                                       select JsonObjectExtensions.Parse(dto)
            select new Fixture
            {
                ServiceDirectory = serviceDirectory,
                ApiName = apiName,
                Name = name,
                OriginalDto = originalDto,
                DtoOverride = dtoOverride
            };

        private static ApiDiagnosticDto ModelToDto(ApiDiagnosticModel model) =>
            new()
            {
                Properties = new ApiDiagnosticDto.DiagnosticContract
                {
                    LoggerId = $"/loggers/{model.LoggerName}",
                    AlwaysLog = model.AlwaysLog.ValueUnsafe(),
                    Sampling = model.Sampling.Map(sampling => new ApiDiagnosticDto.SamplingSettings
                    {
                        SamplingType = sampling.Type,
                        Percentage = sampling.Percentage
                    }).ValueUnsafe()
                }
            };
    }
}
