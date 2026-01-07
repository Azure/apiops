using common;
using common.tests;
using CsCheck;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace publisher.tests;

internal sealed class GetWorkspacePolicyFragmentDtoTests
{
    public static CancellationToken CancellationToken => TestContext.Current?.Execution.CancellationToken ?? CancellationToken.None;

    [Test]
    public async Task Returns_None_when_information_and_policy_files_are_missing()
    {
        var gen = from name in Generator.ResourceName
                  from fixture in Fixture.Generate()
                  select (name, fixture with
                  {
                      GetInformationFileDto = async (_, _, _, _, _, _) =>
                      {
                          await ValueTask.CompletedTask;
                          return Option.None;
                      },
                      GetPolicyFileContents = async (_, _, _, _, _) =>
                      {
                          await ValueTask.CompletedTask;
                          return Option.None;
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (name, fixture) = tuple;
            var getWorkspacePolicyFragmentDto = fixture.Resolve();

            // Act
            var dtoOption = await getWorkspacePolicyFragmentDto(name, ParentChain.Empty, CancellationToken);

            // Assert
            await Assert.That(dtoOption)
                        .IsNone();
        });
    }

    [Test]
    public async Task Returns_information_file_dto_when_policy_file_is_missing()
    {
        var gen = from name in Generator.ResourceName
                  from fixture in Fixture.Generate()
                  from propertyName in Gen.String
                  from propertyValue in Gen.String
                  let informationFileDto = new JsonObject
                  {
                      [propertyName] = propertyValue
                  }
                  select (name, propertyName, propertyValue, fixture with
                  {
                      GetInformationFileDto = async (_, _, _, _, _, _) =>
                      {
                          await ValueTask.CompletedTask;
                          return informationFileDto;
                      },
                      GetPolicyFileContents = async (_, _, _, _, _) =>
                      {
                          await ValueTask.CompletedTask;
                          return Option.None;
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (name, propertyName, expectedPropertyValue, fixture) = tuple;
            var getWorkspacePolicyFragmentDto = fixture.Resolve();

            // Act
            var dtoOption = await getWorkspacePolicyFragmentDto(name, ParentChain.Empty, CancellationToken);

            // Assert that the dto contains the information file property
            var dto = await Assert.That(dtoOption)
                                  .IsSome() ?? [];

            var propertyValueResult = dto.GetStringProperty(propertyName);
            var propertyValue = await Assert.That(propertyValueResult)
                                            .IsSuccess();

            await Assert.That(propertyValue)
                        .IsEqualTo(expectedPropertyValue);
        });
    }

    [Test]
    public async Task Returns_policy_file_dto_when_information_file_is_missing()
    {
        var gen = from name in Generator.ResourceName
                  from policyValue in Gen.String
                  from fixture in Fixture.Generate()
                  select (name, policyValue, fixture with
                  {
                      GetInformationFileDto = async (_, _, _, _, _, _) =>
                      {
                          await ValueTask.CompletedTask;
                          return Option.None;
                      },
                      GetPolicyFileContents = async (_, _, _, _, _) =>
                      {
                          await ValueTask.CompletedTask;
                          return BinaryData.FromString(policyValue);
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (name, expectedPolicyValue, fixture) = tuple;
            var getWorkspacePolicyFragmentDto = fixture.Resolve();

            // Act
            var dtoOption = await getWorkspacePolicyFragmentDto(name, ParentChain.Empty, CancellationToken);

            // Assert that the dto contains the policy file value
            var dto = await Assert.That(dtoOption)
                                  .IsSome() ?? [];

            var policyValueResult = from propertiesJson in dto.GetJsonObjectProperty("properties")
                                    from value in propertiesJson.GetStringProperty("value")
                                    select value;

            var policyValue = await Assert.That(policyValueResult)
                                          .IsSuccess();

            await Assert.That(policyValue)
                        .IsEqualTo(expectedPolicyValue);
        });
    }

    [Test]
    public async Task Merges_information_and_policy_dto_when_both_exist()
    {
        var gen = from name in Generator.ResourceName
                  from informationFilePropertyName in Gen.String
                  from informationFilePropertyValue in Gen.String
                  from policyValue in Gen.String
                  from fixture in Fixture.Generate()
                  let informationFileDto = new JsonObject
                  {
                      [informationFilePropertyName] = informationFilePropertyValue
                  }
                  select (name, informationFilePropertyName, informationFilePropertyValue, policyValue, fixture with
                  {
                      GetInformationFileDto = async (_, _, _, _, _, _) =>
                      {
                          await ValueTask.CompletedTask;
                          return informationFileDto;
                      },
                      GetPolicyFileContents = async (_, _, _, _, _) =>
                      {
                          await ValueTask.CompletedTask;
                          return BinaryData.FromString(policyValue);
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (name, informationFilePropertyName, expectedInformationFilePropertyValue, expectedPolicyValue, fixture) = tuple;
            var getWorkspacePolicyFragmentDto = fixture.Resolve();

            // Act
            var dtoOption = await getWorkspacePolicyFragmentDto(name, ParentChain.Empty, CancellationToken);

            // Assert that the dto contains the information file property
            var dto = await Assert.That(dtoOption)
                                  .IsSome() ?? [];

            var informationFilePropertyValueResult = dto.GetStringProperty(informationFilePropertyName);
            var informationFilePropertyValue = await Assert.That(informationFilePropertyValueResult)
                                                           .IsSuccess();

            await Assert.That(informationFilePropertyValue)
                        .IsEqualTo(expectedInformationFilePropertyValue);

            // Assert that the dto contains the policy file value
            var policyValueResult = from propertiesJson in dto.GetJsonObjectProperty("properties")
                                    from value in propertiesJson.GetStringProperty("value")
                                    select value;

            var policyValue = await Assert.That(policyValueResult)
                                          .IsSuccess();

            await Assert.That(policyValue)
                        .IsEqualTo(expectedPolicyValue);
        });
    }

    private sealed record Fixture
    {
        public required GetCurrentFileOperations GetCurrentFileOperations { get; init; }
        public required GetInformationFileDto GetInformationFileDto { get; init; }
        public required GetPolicyFileContents GetPolicyFileContents { get; init; }

        public GetWorkspacePolicyFragmentDto Resolve()
        {
            var services = new ServiceCollection();

            services.AddSingleton(GetCurrentFileOperations)
                    .AddSingleton(GetInformationFileDto)
                    .AddSingleton(GetPolicyFileContents);

            using var provider = services.BuildServiceProvider();

            return ResourceModule.ResolveGetWorkspacePolicyFragmentDto(provider);
        }

        public static Gen<Fixture> Generate() =>
            Gen.Const(new Fixture
            {
                GetCurrentFileOperations = () => Common.NoOpFileOperations,
                GetInformationFileDto = async (_, _, _, _, _, _) =>
                {
                    await ValueTask.CompletedTask;
                    return Option.None;
                },
                GetPolicyFileContents = async (_, _, _, _, _) =>
                {
                    await ValueTask.CompletedTask;
                    return Option.None;
                }
            });
    }
}