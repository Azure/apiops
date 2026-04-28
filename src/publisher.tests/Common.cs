using common;
using common.tests;
using CsCheck;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace publisher.tests;

internal static class Common
{
    public static FileOperations NoOpFileOperations { get; } = new()
    {
        EnumerateServiceDirectoryFiles = () => [],
        GetSubDirectories = _ => Option.None,
        ReadFile = async (_, _) => Option.None
    };

    public static Gen<Relationships> GenerateRelationships() =>
        from keys in Generator.ResourceKeys
        select Relationships.From(keys.Aggregate(new List<(ResourceKey, ResourceKey)>(),
                                                 (list, key) =>
                                                 {
                                                     // Register the parent -> child edge when applicable.
                                                     switch (key.Parents.ToImmutableArray())
                                                     {
                                                         case []:
                                                             break;
                                                         case [.. var parentParents, var parent]:
                                                             list.Add((new ResourceKey
                                                             {
                                                                 Name = parent.Name,
                                                                 Resource = parent.Resource,
                                                                 Parents = ParentChain.From(parentParents)
                                                             }, key));
                                                             break;
                                                     }

                                                     return list;
                                                 }),
                              cancellationToken: CancellationToken.None);

    public static IConfiguration ToConfiguration(IEnumerable<(string, string)> pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(pair => KeyValuePair.Create(pair.Item1, (string?)pair.Item2)))
            .Build();
}

internal sealed class GetCurrentFileOperationsTests
{
    [Test]
    public async Task Returns_local_file_operations_when_no_commit_id_was_passed()
    {
        var gen = from fixture in Fixture.Generate()
                  let localMarkerFile = new FileInfo("local.txt")
                  let localFileOperations = Common.NoOpFileOperations with
                  {
                      EnumerateServiceDirectoryFiles = () => ImmutableHashSet.Create(FileInfoModule.Comparer, localMarkerFile)
                  }
                  select (localMarkerFile, fixture with
                  {
                      CommitIdWasPassed = () => false,
                      GetCurrentCommitFileOperations = () =>
                      {
                          return Option.Some(Common.NoOpFileOperations);
                      },
                      GetLocalFileOperations = () => localFileOperations
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (localMarkerFile, fixture) = tuple;
            var getCurrentFileOperations = fixture.Resolve();

            // Act
            var fileOperations = getCurrentFileOperations();
            var files = fileOperations.EnumerateServiceDirectoryFiles();

            // Assert that the local file operations were used
            await Assert.That(files)
                        .Contains(localMarkerFile);
        });
    }

    [Test]
    public async Task Returns_commit_file_operations_when_a_commit_id_was_passed()
    {
        var gen = from fixture in Fixture.Generate()
                  let commitMarkerFile = new FileInfo("commit.txt")
                  let commitFileOperations = Common.NoOpFileOperations with
                  {
                      EnumerateServiceDirectoryFiles = () => ImmutableHashSet.Create(FileInfoModule.Comparer, commitMarkerFile)
                  }
                  select (commitMarkerFile, fixture with
                  {
                      CommitIdWasPassed = () => true,
                      GetCurrentCommitFileOperations = () =>
                      {
                          return Option.Some(commitFileOperations);
                      },
                      GetLocalFileOperations = () => Common.NoOpFileOperations
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (commitMarkerFile, fixture) = tuple;
            var getCurrentFileOperations = fixture.Resolve();

            // Act
            var fileOperations = getCurrentFileOperations();
            var files = fileOperations.EnumerateServiceDirectoryFiles();

            // Assert that the commit file operations were used
            await Assert.That(files)
                        .Contains(commitMarkerFile);
        });
    }

    [Test]
    public async Task Throws_when_a_commit_id_was_passed_but_commit_file_operations_are_missing()
    {
        var gen = from fixture in Fixture.Generate()
                  select fixture with
                  {
                      CommitIdWasPassed = () => true,
                      GetCurrentCommitFileOperations = () => Option.None
                  };

        await gen.SampleAsync(async fixture =>
        {
            // Arrange
            var getCurrentFileOperations = fixture.Resolve();

            // Assert that an exception is thrown
            await Assert.That(() => getCurrentFileOperations())
                        .Throws<InvalidOperationException>();
        });
    }

    private sealed record Fixture
    {
        public required CommitIdWasPassed CommitIdWasPassed { get; init; }
        public required GetCurrentCommitFileOperations GetCurrentCommitFileOperations { get; init; }
        public required GetLocalFileOperations GetLocalFileOperations { get; init; }

        public GetCurrentFileOperations Resolve()
        {
            var services = new ServiceCollection();

            services.AddSingleton(CommitIdWasPassed)
                    .AddSingleton(GetCurrentCommitFileOperations)
                    .AddSingleton(GetLocalFileOperations);

            using var provider = services.BuildServiceProvider();

            return CommonModule.ResolveGetCurrentFileOperations(provider);
        }

        public static Gen<Fixture> Generate() =>
            from wasCommitPassed in Gen.Bool
            select new Fixture
            {
                CommitIdWasPassed = () => wasCommitPassed,
                GetCurrentCommitFileOperations = () => Option.None,
                GetLocalFileOperations = () => Common.NoOpFileOperations
            };
    }
}

internal sealed class IsDryRunTests
{
    [Test]
    public async Task Returns_false_when_the_setting_is_missing()
    {
        var gen = from fixture in Fixture.Generate()
                  select fixture;

        await gen.SampleAsync(async fixture =>
        {
            // Arrange
            var isDryRun = fixture.Resolve();

            // Act
            var enabled = isDryRun();

            // Assert
            await Assert.That(enabled)
                        .IsFalse();
        });
    }

    [Test]
    public async Task Returns_true_when_the_setting_is_true()
    {
        var gen = from fixture in Fixture.Generate()
                  from value in Generator.RandomizeCapitalization("true")
                  let configuration = Common.ToConfiguration([("DRY_RUN", value)])
                  select fixture with
                  {
                      Configuration = configuration
                  };

        await gen.SampleAsync(async fixture =>
        {
            // Arrange
            var isDryRun = fixture.Resolve();

            // Act
            var enabled = isDryRun();

            // Assert
            await Assert.That(enabled)
                        .IsTrue();
        });
    }

    [Test]
    public async Task Returns_false_when_the_setting_is_false()
    {
        var gen = from fixture in Fixture.Generate()
                  from value in Generator.RandomizeCapitalization("false")
                  let configuration = Common.ToConfiguration([("DRY_RUN", value)])
                  select fixture with
                  {
                      Configuration = configuration
                  };

        await gen.SampleAsync(async fixture =>
        {
            // Arrange
            var isDryRun = fixture.Resolve();

            // Act
            var enabled = isDryRun();

            // Assert
            await Assert.That(enabled)
                        .IsFalse();
        });
    }

    private sealed record Fixture
    {
        public required IConfiguration Configuration { get; init; }

        public IsDryRun Resolve()
        {
            var services = new ServiceCollection();

            services.AddSingleton(Configuration)
                    .AddNullLogger();

            using var provider = services.BuildServiceProvider();

            return CommonModule.ResolveIsDryRun(provider);
        }

        public static Gen<Fixture> Generate() =>
            from configuration in Generator.Configuration
            select new Fixture
            {
                Configuration = configuration
            };
    }
}