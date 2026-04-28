using common;
using common.tests;
using CsCheck;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace publisher.tests;

internal sealed class GetConfigurationOverrideTests
{
    private static CancellationToken CancellationToken =>
        TestContext.Current?.Execution.CancellationToken ?? CancellationToken.None;

    [Test]
    public async Task Returns_None_when_no_override_exists()
    {
        var gen = from resourceKey in Generator.ResourceKey
                  from fixture in Fixture.Generate()
                  let configuration = Common.ToConfiguration([])
                  select (resourceKey, fixture with
                  {
                      Configuration = configuration
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (resourceKey, fixture) = tuple;
            var getConfigurationOverride = fixture.Resolve();

            // Act
            var overrideOption = await getConfigurationOverride(resourceKey, CancellationToken);

            // Assert that no override is returned
            await Assert.That(overrideOption)
                        .IsNone();
        });
    }

    [Test]
    public async Task Returns_override_when_it_exists_at_the_root()
    {
        var gen = from resourceKey in Generator.ResourceKey
                  where resourceKey.Parents.Count == 0
                  from overriddenDisplayName in Gen.String
                  from fixture in Fixture.Generate()
                  let resource = resourceKey.Resource
                  let configuration = Common.ToConfiguration([
                      ($"{resource.ConfigurationKey}:0:name", resourceKey.Name.ToString()),
                      ($"{resource.ConfigurationKey}:0:properties:displayName", overriddenDisplayName)])
                  select (resourceKey, overriddenDisplayName, fixture with
                  {
                      Configuration = configuration
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (resourceKey, overriddenDisplayName, fixture) = tuple;
            var getConfigurationOverride = fixture.Resolve();

            // Act
            var overrideOption = await getConfigurationOverride(resourceKey, CancellationToken);

            // Assert that an override is returned
            var overrideJson = await Assert.That(overrideOption)
                                           .IsSome();

            // Assert that the override has the resource name
            var nameResult = overrideJson.GetStringProperty("name");
            var name = await Assert.That(nameResult)
                                   .IsSuccess();
            await Assert.That(name)
                        .IsEqualTo(resourceKey.Name.ToString());

            // Assert that the override has the overriden display name
            var displayNameResult = from propertiesJson in overrideJson.GetJsonObjectProperty("properties")
                                    from displayNameString in propertiesJson.GetStringProperty("displayName")
                                    select displayNameString;

            var displayName = await Assert.That(displayNameResult)
                                          .IsSuccess();

            await Assert.That(displayName)
                        .IsEqualTo(overriddenDisplayName);
        });
    }

    [Test]
    public async Task Returns_override_when_it_exists_under_a_parent_chain()
    {
        var gen = from resourceKey in Generator.ResourceKey
                  where resourceKey.Parents.Count > 0
                  from overriddenDisplayName in Gen.String
                  from fixture in Fixture.Generate()
                  let parentPairs = resourceKey.Parents.Select((parent, index) =>
                  {
                      var (_, parentName) = parent;

                      var parentPathResources = resourceKey.Parents
                                                           .Take(index + 1)
                                                           .Select(pair => pair.Resource);

                      var parentPath = calculateJsonPath(parentPathResources);

                      return ($"{parentPath}:name", parentName.ToString());
                  })
                  let basePath = calculateJsonPath([.. resourceKey.Parents.Select(pair => pair.Resource)])
                  let configuration = Common.ToConfiguration([
                      .. parentPairs,
                      ($"{basePath}:{resourceKey.Resource.ConfigurationKey}:0:name", resourceKey.Name.ToString()),
                      ($"{basePath}:{resourceKey.Resource.ConfigurationKey}:0:properties:displayName", overriddenDisplayName)
                      ])
                  select (resourceKey, overriddenDisplayName, fixture with
                  {
                      Configuration = configuration
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (resourceKey, overriddenDisplayName, fixture) = tuple;
            var getConfigurationOverride = fixture.Resolve();

            // Act
            var overrideOption = await getConfigurationOverride(resourceKey, CancellationToken);

            // Assert that an override is returned
            var overrideJson = await Assert.That(overrideOption)
                                           .IsSome();

            // Assert that the override has the resource name
            var nameResult = overrideJson.GetStringProperty("name");
            var name = await Assert.That(nameResult)
                                   .IsSuccess();
            await Assert.That(name)
                        .IsEqualTo(resourceKey.Name.ToString());

            // Assert that the override has the overriden display name
            var displayNameResult = from propertiesJson in overrideJson.GetJsonObjectProperty("properties")
                                    from displayNameString in propertiesJson.GetStringProperty("displayName")
                                    select displayNameString;

            var displayName = await Assert.That(displayNameResult)
                                          .IsSuccess();

            await Assert.That(displayName)
                        .IsEqualTo(overriddenDisplayName);

        });

        static string calculateJsonPath(IEnumerable<IResource> parents) =>
            parents.Aggregate(string.Empty,
                              (path, parent) =>
                              {
                                  var prefix = string.IsNullOrEmpty(path)
                                      ? string.Empty
                                      : $"{path}:";

                                  return $"{prefix}{parent.ConfigurationKey}:0";
                              });
    }

    [Test]
    public async Task Api_override_removes_revision_properties()
    {
        var gen = from resourceKey in Generator.GenerateResourceKey(ApiResource.Instance)
                  from fixture in Fixture.Generate()
                  let apiResource = (IResource)ApiResource.Instance
                  let configuration = Common.ToConfiguration([
                        ($"{apiResource.ConfigurationKey}:0:name", resourceKey.Name.ToString()),
                        ($"{apiResource.ConfigurationKey}:0:properties:apiRevision", "2"),
                        ($"{apiResource.ConfigurationKey}:0:properties:isCurrent", "false")
                      ])
                  select (resourceKey, fixture with
                  {
                      Configuration = configuration
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (resourceKey, fixture) = tuple;
            var getConfigurationOverride = fixture.Resolve();

            // Act
            var overrideOption = await getConfigurationOverride(resourceKey, CancellationToken);

            // Assert that an override is returned
            var overrideJson = await Assert.That(overrideOption)
                                           .IsSome();

            // Assert that revision properties are removed
            var propertiesResult = overrideJson.GetJsonObjectProperty("properties");
            var properties = await Assert.That(propertiesResult)
                                         .IsSuccess();

            var propertiesDictionary = properties?.ToImmutableDictionary() ?? [];

            await Assert.That(propertiesDictionary)
                        .DoesNotContainKey("apiRevision");

            await Assert.That(propertiesDictionary)
                        .DoesNotContainKey("isCurrent");
        });
    }

    private sealed record Fixture
    {
        public required IConfiguration Configuration { get; init; }

        public GetConfigurationOverride Resolve()
        {
            var services = new ServiceCollection();

            services.AddSingleton(Configuration)
                    .AddTestActivitySource()
                    .AddNullLogger();

            using var provider = services.BuildServiceProvider();

            return ConfigurationModule.ResolveGetConfigurationOverride(provider);
        }

        public static Gen<Fixture> Generate() =>
            from configuration in Generator.Configuration
            select new Fixture
            {
                Configuration = configuration
            };
    }
}