using common;
using common.tests;
using CsCheck;
using FluentAssertions;
using LanguageExt;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Xunit;

namespace publisher.unit.tests;

public class FindConfigurationSectionTests
{
    [Fact]
    public void No_argments_always_returns_none()
    {
        var generator = Fixture.Generate();

        generator.Sample(fixture =>
        {
            var result = fixture.FindConfigurationSection();

            result.Should().BeNone();
        });
    }

    [Fact]
    public void Return_none_when_section_does_not_exist()
    {
        var generator = from jsonObject in Generator.JsonObject
                        from missingSection in Generator.NonEmptyString
                        where jsonObject.ContainsKey(missingSection) is false
                        from fixture in Fixture.Generate()
                        let updatedFixture = fixture with { ConfigurationJson = fixture.ConfigurationJson with { Value = jsonObject } }
                        select (updatedFixture, missingSection);

        generator.Sample(x =>
        {
            var (fixture, missingSection) = x;

            var result = fixture.FindConfigurationSection(missingSection);

            result.Should().BeNone();
        });
    }

    [Fact]
    public void Returns_existing_section()
    {
        var generator = from sections in Generator.AlphaNumericStringBetween(5, 10).Array[1, 5]
                        from expectedValue in Generator.JsonObject
                        from jsonObject in sections.Select(sectionName => from jsonObject in Generator.JsonObject
                                                                          select KeyValuePair.Create(sectionName, jsonObject))
                                                   .Reverse()
                                                   .Aggregate(Gen.Const(expectedValue),
                                                              (jsonObjectGen, kvpGen) => from previousJsonObject in jsonObjectGen
                                                                                         from newJsonObject in Generator.JsonObject
                                                                                         from kvp in kvpGen
                                                                                         select newJsonObject.SetProperty(kvp.Key, previousJsonObject))
                        from fixture in Fixture.Generate()
                        let updatedFixture = fixture with { ConfigurationJson = fixture.ConfigurationJson with { Value = jsonObject } }
                        select (updatedFixture, sections, expectedValue);

        generator.Sample(x =>
        {
            // Arrange
            var (fixture, sections, expectedValue) = x;

            // Act
            var result = fixture.FindConfigurationSection(sections);

            // Assert
            var expectedValueString = expectedValue.ToJsonString();
            var actualValueString = result.Map(json => json.ToJsonString());
            actualValueString.Should().BeSome(expected: expectedValueString);
        });
    }

    private sealed record Fixture
    {
        public required ConfigurationJson ConfigurationJson { get; init; }

        public static Gen<Fixture> Generate() =>
            from configurationJson in GenerateConfigurationJson()
            select new Fixture
            {
                ConfigurationJson = configurationJson
            };

        private static Gen<ConfigurationJson> GenerateConfigurationJson() =>
            from jsonObject in Generator.JsonObject
            select new ConfigurationJson
            {
                Value = jsonObject
            };

        public Option<JsonObject> FindConfigurationSection(params string[] sectionNames)
        {
            var serviceProvider = GetServiceProvider();

            var findConfigurationSection = ConfigurationJsonModule.GetFindConfigurationSection(serviceProvider);

            return findConfigurationSection(sectionNames);
        }

        private IServiceProvider GetServiceProvider()
        {
            var services = new ServiceCollection();

            services.AddSingleton(ConfigurationJson);

            return services.BuildServiceProvider();
        }
    }
}