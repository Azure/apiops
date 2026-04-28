using common;
using common.tests;
using CsCheck;
using DotNext;
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

internal sealed class GetCurrentCommitIdTests
{
    [Test]
    public async Task Returns_None_when_setting_is_missing()
    {
        var gen = from fixture in Fixture.Generate()
                  let configuration =
                    new ConfigurationBuilder()
                        .AddInMemoryCollection([])
                        .Build()
                  select fixture with { Configuration = configuration };

        await gen.SampleAsync(async fixture =>
        {
            // Arrange
            var getCurrentCommitId = fixture.Resolve();

            // Act
            var commitIdOption = getCurrentCommitId();

            // Assert that no commit ID is returned
            await Assert.That(commitIdOption)
                        .IsNone();
        });
    }

    [Test]
    public async Task Returns_None_when_setting_is_empty()
    {
        var gen = from fixture in Fixture.Generate()
                  let configuration =
                    new ConfigurationBuilder()
                        .AddInMemoryCollection([KeyValuePair.Create("COMMIT_ID", (string?)string.Empty)])
                        .Build()
                  select fixture with { Configuration = configuration };

        await gen.SampleAsync(async fixture =>
        {
            // Arrange
            var getCurrentCommitId = fixture.Resolve();

            // Act
            var commitIdOption = getCurrentCommitId();

            // Assert that no commit ID is returned
            await Assert.That(commitIdOption)
                        .IsNone();
        });
    }

    [Test]
    public async Task Returns_Some_when_setting_is_present()
    {
        var gen = from commitIdString in Gen.String
                  where string.IsNullOrWhiteSpace(commitIdString) is false
                  let commitId = CommitId.From(commitIdString)
                                         .IfErrorThrow()
                  from fixture in Fixture.Generate()
                  let configuration =
                    new ConfigurationBuilder()
                        .AddInMemoryCollection([KeyValuePair.Create("COMMIT_ID", (string?)commitIdString)])
                        .Build()
                  select (commitId, fixture with { Configuration = configuration });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (expectedCommitId, fixture) = tuple;
            var getCurrentCommitId = fixture.Resolve();

            // Act
            var commitIdOption = getCurrentCommitId();

            // Assert that the expected commit ID is returned
            var commitId = await Assert.That(commitIdOption)
                                       .IsSome();

            await Assert.That(commitId)
                        .IsEqualTo(expectedCommitId);
        });
    }

    private sealed record Fixture
    {
        public required IConfiguration Configuration { get; init; }

        public GetCurrentCommitId Resolve()
        {
            var services = new ServiceCollection();

            services.AddSingleton(Configuration)
                    .AddNullLogger();

            using var provider = services.BuildServiceProvider();

            return GitModule.ResolveGetCurrentCommitId(provider);
        }

        public static Gen<Fixture> Generate() =>
            Gen.Const(new Fixture
            {
                Configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build()
            });
    }
}

internal sealed class CommitIdWasPassedTests
{
    [Test]
    public async Task Returns_false_when_the_current_commit_id_is_None()
    {
        var gen = from fixture in Fixture.Generate()
                  select fixture with
                  {
                      GetCurrentCommitId = () => Option.None
                  };

        await gen.SampleAsync(async fixture =>
        {
            // Arrange
            var commitIdWasPassed = fixture.Resolve();

            // Act
            var result = commitIdWasPassed();

            // Assert that commit ID is not passed
            await Assert.That(result)
                        .IsFalse();
        });
    }

    [Test]
    public async Task Returns_true_when_the_current_commit_id_is_Some()
    {
        var gen = from commitIdString in Gen.String
                  where string.IsNullOrWhiteSpace(commitIdString) is false
                  let commitId = CommitId.From(commitIdString)
                                         .IfErrorThrow()
                  from fixture in Fixture.Generate()
                  select fixture with
                  {
                      GetCurrentCommitId = () => commitId
                  };

        await gen.SampleAsync(async fixture =>
        {
            // Arrange
            var commitIdWasPassed = fixture.Resolve();

            // Act
            var result = commitIdWasPassed();

            // Assert that commit ID is passed
            await Assert.That(result)
                        .IsTrue();
        });
    }

    private sealed record Fixture
    {
        public required GetCurrentCommitId GetCurrentCommitId { get; init; }

        public CommitIdWasPassed Resolve()
        {
            var services = new ServiceCollection();

            services.AddSingleton(GetCurrentCommitId);

            using var provider = services.BuildServiceProvider();

            return GitModule.ResolveCommitIdWasPassed(provider);
        }

        public static Gen<Fixture> Generate() =>
            from commitIdString in Gen.String
            let commitIdOption = CommitId.From(commitIdString)
                                         .ToOption()
            select new Fixture
            {
                GetCurrentCommitId = () => commitIdOption
            };
    }
}

internal sealed class GetCurrentCommitFileOperationsTests
{
    public static CancellationToken CancellationToken => TestContext.Current?.Execution.CancellationToken ?? CancellationToken.None;

    [Test]
    public async Task Returns_None_when_the_current_commit_id_is_None()
    {
        var gen = from fixture in Fixture.Generate()
                  select fixture with
                  {
                      GetCurrentCommitId = () => Option.None
                  };

        await gen.SampleAsync(async fixture =>
        {
            // Arrange
            var getCurrentCommitFileOperations = fixture.Resolve();

            // Act
            var fileOperationsOption = getCurrentCommitFileOperations();

            // Assert that no file operations are returned
            await Assert.That(fileOperationsOption)
                        .IsNone();
        });
    }

    [Test]
    public async Task Uses_the_selected_commit_id_to_read_file_from_that_commit()
    {
        var gen = from commitCount in Gen.Int[1, 8]
                  from commitIndex in Gen.Int[0, commitCount - 1]
                  from fixture in Fixture.Generate()
                  from serviceDirectoryInfo in Generator.DirectoryInfo
                  from fileName in Generator.FileName
                  let filePath = Path.Combine(serviceDirectoryInfo.FullName, fileName)
                  let file = new FileInfo(filePath)
                  from fileTexts in Gen.String.HashSetOf(commitCount)
                  select (commitIndex, file, fileTexts, fixture with
                  {
                      ServiceDirectory = ServiceDirectory.FromDirectoryInfo(serviceDirectoryInfo)
                  });

        await gen.SampleAsync(async tuple =>
        {
            var (commitIndex, file, fileTexts, fixture) = tuple;
            var repositoryDirectory = fixture.ServiceDirectory.ToDirectoryInfo();

            try
            {
                // Arrange
                var authorName = "john.smith";
                var authorEmail = "john.smith@company.com";
                var commits = await writeCommits(file, fileTexts, repositoryDirectory, authorName, authorEmail, CancellationToken);
                var updatedFixture = fixture with
                {
                    GetCurrentCommitId = () => commits[commitIndex]
                };
                var getCurrentCommitFileOperations = updatedFixture.Resolve();

                // Act
                var fileOperationsOption = getCurrentCommitFileOperations();

                // Assert that file operations are against the correct commit
                var fileOperations = await Assert.That(fileOperationsOption)
                                                 .IsSome();

                var contentsOption = await fileOperations!.ReadFile(file, CancellationToken);

                var contents = await Assert.That(contentsOption)
                                           .IsSome();

                await Assert.That(contents?.ToString() ?? string.Empty)
                            .IsEqualTo(fileTexts.ElementAt(commitIndex));
            }
            finally
            {
                repositoryDirectory.DeleteIfExists();
            }
        }, iter: 20);

        static async Task<ImmutableArray<CommitId>> writeCommits(FileInfo file, IEnumerable<string> fileTexts, DirectoryInfo repositoryDirectory, string authorName, string authorEmail, CancellationToken cancellationToken)
        {
            var commitIds = new List<CommitId>();

            await fileTexts.Select((text, index) => (text, index))
                           .IterTask(async tuple =>
                           {
                               var (text, index) = tuple;

                               file.Directory!.Create();
                               await File.WriteAllTextAsync(file.FullName, text, cancellationToken);

                               var message = $"Commit {index}";
                               var signatureDate = DateTimeOffset.UtcNow;
                               var commit = index == 0
                                           ? common.GitModule.InitializeRepository(repositoryDirectory, message, authorName, authorEmail, signatureDate)
                                           : common.GitModule.CommitChanges(repositoryDirectory, message, authorName, authorEmail, signatureDate);

                               commitIds.Add(CommitId.From(commit));
                           }, cancellationToken);

            return [.. commitIds];
        }
    }

    private sealed record Fixture
    {
        public required GetCurrentCommitId GetCurrentCommitId { get; init; }
        public required ServiceDirectory ServiceDirectory { get; init; }

        public GetCurrentCommitFileOperations Resolve()
        {
            var services = new ServiceCollection();

            services.AddSingleton(GetCurrentCommitId)
                    .AddSingleton(ServiceDirectory);

            using var provider = services.BuildServiceProvider();

            return GitModule.ResolveGetCurrentCommitFileOperations(provider);
        }

        public static Gen<Fixture> Generate() =>
            from directory in Generator.DirectoryInfo
            from commitId in from commitIdString in Gen.String
                             select CommitId.From(commitIdString)
                                            .ToOption()
            select new Fixture
            {
                GetCurrentCommitId = () => commitId,
                ServiceDirectory = ServiceDirectory.FromDirectoryInfo(directory)
            };
    }
}

internal sealed class GetPreviousCommitIdTests
{
    public static CancellationToken CancellationToken => TestContext.Current?.Execution.CancellationToken ?? CancellationToken.None;

    [Test]
    public async Task Returns_None_when_the_current_commit_id_is_None()
    {
        var gen = from fixture in Fixture.Generate()
                  select fixture with
                  {
                      GetCurrentCommitId = () => Option.None
                  };

        await gen.SampleAsync(async fixture =>
        {
            // Arrange
            var getPreviousCommitId = fixture.Resolve();

            // Act
            var previousCommitIdOption = getPreviousCommitId();

            // Assert
            await Assert.That(previousCommitIdOption)
                        .IsNone();
        });
    }

    [Test]
    public async Task Returns_the_previous_commit_id_when_the_current_commit_has_a_parent_otherwise_None()
    {
        var gen = from repositoryDirectory in Generator.DirectoryInfo
                  let serviceDirectoryInfo = new DirectoryInfo(Path.Combine(repositoryDirectory.FullName, "services"))
                  let serviceDirectory = ServiceDirectory.FromDirectoryInfo(serviceDirectoryInfo)
                  from commitCount in Gen.Int[1, 10]
                  from commitIndex in Gen.Int[0, commitCount - 1]
                  from fixture in Fixture.Generate()
                  select (repositoryDirectory, serviceDirectoryInfo, serviceDirectory, commitCount, commitIndex, fixture with
                  {
                      ServiceDirectory = serviceDirectory
                  });

        await gen.SampleAsync(async tuple =>
        {
            var (repositoryDirectory, serviceDirectoryInfo, serviceDirectory, commitCount, commitIndex, fixture) = tuple;

            try
            {
                // Arrange
                var authorName = "john.smith";
                var authorEmail = "john.smith@company.com";
                var commits = await writeCommits(commitCount, repositoryDirectory, serviceDirectoryInfo, authorName, authorEmail, CancellationToken);

                var updatedFixture = fixture with
                {
                    GetCurrentCommitId = () => commits[commitIndex]
                };

                var getPreviousCommitId = updatedFixture.Resolve();

                // Act
                var previousCommitIdOption = getPreviousCommitId();

                // Assert
                if (commitIndex == 0)
                {
                    await Assert.That(previousCommitIdOption)
                                .IsNone();
                }
                else
                {
                    var previousCommitId = await Assert.That(previousCommitIdOption)
                                                       .IsSome();

                    await Assert.That(previousCommitId)
                                .IsEqualTo(commits[commitIndex - 1]);
                }
            }
            finally
            {
                repositoryDirectory.DeleteIfExists();
            }
        }, iter: 20);

        static async Task<ImmutableArray<CommitId>> writeCommits(int commitCount, DirectoryInfo repositoryDirectory, DirectoryInfo serviceDirectoryInfo, string authorName, string authorEmail, CancellationToken cancellationToken)
        {
            var commitIds = new List<CommitId>();

            await Enumerable.Range(0, commitCount)
                            .IterTask(async index =>
                            {
                                var file = new FileInfo(Path.Combine(serviceDirectoryInfo.FullName, $"commit-{index}.txt"));

                                file.Directory!.Create();
                                await File.WriteAllTextAsync(file.FullName, $"commit-{index}", cancellationToken);

                                var message = $"Commit {index}";
                                var signatureDate = DateTimeOffset.UtcNow;
                                var commit = index == 0
                                    ? common.GitModule.InitializeRepository(repositoryDirectory, message, authorName, authorEmail, signatureDate)
                                    : common.GitModule.CommitChanges(repositoryDirectory, message, authorName, authorEmail, signatureDate);

                                commitIds.Add(CommitId.From(commit));
                            }, cancellationToken);
                            
            return [.. commitIds];
        }
    }

    private sealed record Fixture
    {
        public required GetCurrentCommitId GetCurrentCommitId { get; init; }
        public required ServiceDirectory ServiceDirectory { get; init; }

        public GetPreviousCommitId Resolve()
        {
            var services = new ServiceCollection();

            services.AddSingleton(GetCurrentCommitId)
                    .AddSingleton(ServiceDirectory);

            using var provider = services.BuildServiceProvider();

            return GitModule.ResolveGetPreviousCommitId(provider);
        }

        public static Gen<Fixture> Generate() =>
            from directory in Generator.DirectoryInfo
            from commitId in from commitIdString in Gen.String
                             select CommitId.From(commitIdString)
                                            .ToOption()
            select new Fixture
            {
                GetCurrentCommitId = () => commitId,
                ServiceDirectory = ServiceDirectory.FromDirectoryInfo(directory)
            };
    }
}

internal sealed class GetPreviousCommitFileOperationsTests
{
    public static CancellationToken CancellationToken => TestContext.Current?.Execution.CancellationToken ?? CancellationToken.None;

    [Test]
    public async Task Returns_None_when_the_previous_commit_id_is_None()
    {
        var gen = from fixture in Fixture.Generate()
                  select fixture with
                  {
                      GetPreviousCommitId = () => Option.None
                  };

        await gen.SampleAsync(async fixture =>
        {
            // Arrange
            var getPreviousCommitFileOperations = fixture.Resolve();

            // Act
            var fileOperationsOption = getPreviousCommitFileOperations();

            // Assert
            await Assert.That(fileOperationsOption)
                        .IsNone();
        });
    }

    [Test]
    public async Task Reads_files_from_the_previous_commit()
    {
        var gen = from repositoryDirectory in Generator.DirectoryInfo
                  let serviceDirectoryInfo = new DirectoryInfo(Path.Combine(repositoryDirectory.FullName, "services"))
                  let serviceDirectory = ServiceDirectory.FromDirectoryInfo(serviceDirectoryInfo)
                  from fileName in Generator.FileName
                  let filePath = Path.Combine(serviceDirectoryInfo.FullName, fileName)
                  let file = new FileInfo(filePath)
                  from commitCount in Gen.Int[2, 8]
                  from commitIndex in Gen.Int[1, commitCount - 1]
                  from fileTexts in Gen.String.Array[commitCount, commitCount]
                  from fixture in Fixture.Generate()
                  select (repositoryDirectory, serviceDirectory, file, commitIndex, fileTexts, fixture with
                  {
                      ServiceDirectory = serviceDirectory
                  });

        await gen.SampleAsync(async tuple =>
        {
            var (repositoryDirectory, serviceDirectory, file, commitIndex, fileTexts, fixture) = tuple;

            try
            {
                // Arrange
                var authorName = "john.smith";
                var authorEmail = "john.smith@company.com";
                var commits = await writeCommits(file, fileTexts, repositoryDirectory, authorName, authorEmail, CancellationToken);

                var previousCommitId = commits[commitIndex - 1];

                var updatedFixture = fixture with
                {
                    GetPreviousCommitId = () => previousCommitId
                };

                var getPreviousCommitFileOperations = updatedFixture.Resolve();

                // Act
                var fileOperationsOption = getPreviousCommitFileOperations();

                // Assert
                var fileOperations = await Assert.That(fileOperationsOption)
                                                 .IsSome();

                var contentsOption = await fileOperations!.ReadFile(file, CancellationToken);

                var contents = await Assert.That(contentsOption)
                                           .IsSome();

                await Assert.That(contents?.ToString() ?? string.Empty)
                            .IsEqualTo(fileTexts[commitIndex - 1]);
            }
            finally
            {
                repositoryDirectory.DeleteIfExists();
            }
        }, iter: 20);

        static async Task<ImmutableArray<CommitId>> writeCommits(FileInfo file, string[] fileTexts, DirectoryInfo repositoryDirectory, string authorName, string authorEmail, CancellationToken cancellationToken)
        {
            var commitIds = new List<CommitId>(capacity: fileTexts.Length);

            for (var index = 0; index < fileTexts.Length; index++)
            {
                file.Directory!.Create();
                await File.WriteAllTextAsync(file.FullName, fileTexts[index], cancellationToken);

                var message = $"Commit {index}";
                var signatureDate = DateTimeOffset.UtcNow;
                var commit = index == 0
                    ? common.GitModule.InitializeRepository(repositoryDirectory, message, authorName, authorEmail, signatureDate)
                    : common.GitModule.CommitChanges(repositoryDirectory, message, authorName, authorEmail, signatureDate);

                commitIds.Add(CommitId.From(commit));
            }

            return [.. commitIds];
        }
    }

    private sealed record Fixture
    {
        public required GetPreviousCommitId GetPreviousCommitId { get; init; }
        public required ServiceDirectory ServiceDirectory { get; init; }

        public GetPreviousCommitFileOperations Resolve()
        {
            var services = new ServiceCollection();

            services.AddSingleton(GetPreviousCommitId)
                    .AddSingleton(ServiceDirectory);

            using var provider = services.BuildServiceProvider();

            return GitModule.ResolveGetPreviousCommitFileOperations(provider);
        }

        public static Gen<Fixture> Generate() =>
            from directory in Generator.DirectoryInfo
            from commitId in from commitIdString in Gen.String
                             select CommitId.From(commitIdString)
                                            .ToOption()
            select new Fixture
            {
                GetPreviousCommitId = () => commitId,
                ServiceDirectory = ServiceDirectory.FromDirectoryInfo(directory)
            };
    }
}

internal sealed class ListServiceDirectoryFilesModifiedByCurrentCommitTests
{
    public static CancellationToken CancellationToken => TestContext.Current?.Execution.CancellationToken ?? CancellationToken.None;

    [Test]
    public async Task Returns_files_modified_by_commit()
    {
        var gen = from repositoryDirectory in Generator.DirectoryInfo
                  let serviceDirectoryInfo = new DirectoryInfo(Path.Combine(repositoryDirectory.FullName, "services"))
                  let serviceDirectory = ServiceDirectory.FromDirectoryInfo(serviceDirectoryInfo)
                  let fileGen = from name in Generator.FileName
                                let filePath = Path.Combine(serviceDirectoryInfo.FullName, name)
                                select new FileInfo(filePath)
                  from commitCount in Gen.Int[1, 10]
                  from commitIndex in Gen.Int[0, commitCount - 1]
                  from commitDictionaries in
                      Enumerable.Range(0, commitCount)
                                .Aggregate(Gen.Const(ImmutableArray<ImmutableDictionary<FileInfo, string>>.Empty),
                                           (gen, index) =>
                                           {
                                               if (index == 0)
                                               {
                                                   return from dictionary in Generator.DictionaryOf(fileGen, Gen.String, minimumLength: 1, maximumLength: 10, FileInfoModule.Comparer)
                                                          select ImmutableArray.Create(dictionary);
                                               }
                                               else
                                               {
                                                   return from previousDictionaries in gen
                                                          let previousDictionary = previousDictionaries.Last()
                                                          from files in Gen.OneOf(fileGen, Gen.OneOfConst([.. previousDictionary.Keys])).Array[1, 10]
                                                          from keyValuePairs in Generator.Traverse(files.Distinct(FileInfoModule.Comparer),
                                                                                                   file => from contents in
                                                                                                               previousDictionary.Find(file)
                                                                                                                                 .Map(contents => Gen.OneOf(Gen.Const(contents), Gen.String))
                                                                                                                                 .IfNone(() => Gen.String)
                                                                                                           select KeyValuePair.Create(file, contents))
                                                          let newDictionary = keyValuePairs.ToImmutableDictionary(FileInfoModule.Comparer)
                                                          // Ensure at least one change from the previous commit
                                                          where newDictionary.Count != previousDictionary.Count
                                                                || newDictionary.Keys.Except(previousDictionary.Keys, FileInfoModule.Comparer).Any()
                                                                || previousDictionary.Keys.Except(newDictionary.Keys, FileInfoModule.Comparer).Any()
                                                                || newDictionary.Keys.Intersect(previousDictionary.Keys, FileInfoModule.Comparer)
                                                                                     .Any(file => newDictionary[file] != previousDictionary[file])
                                                          select previousDictionaries.Add(keyValuePairs.ToImmutableDictionary(FileInfoModule.Comparer));
                                               }
                                           })
                  from fixture in Fixture.Generate()
                  select (repositoryDirectory, commitDictionaries, commitIndex, fixture with
                  {
                      ServiceDirectory = serviceDirectory
                  });

        await gen.SampleAsync(async tuple =>
        {
            var (repositoryDirectory, commitDictionaries, commitIndex, fixture) = tuple;
            try
            {
                // Arrange
                var authorName = "john.smith";
                var authorEmail = "john.smith@company.com";
                var commits = await writeCommits(commitDictionaries, repositoryDirectory, authorName, authorEmail, CancellationToken);
                var updatedFixture = fixture with
                {
                    GetCurrentCommitId = () => commits[commitIndex]
                };
                var listFiles = updatedFixture.Resolve();

                // Act
                var filesOption = listFiles();

                // Assert that Git actions match what we expect
                var currentFiles = commitDictionaries[commitIndex];
                var previousFiles = commitIndex == 0 ? ImmutableDictionary<FileInfo, string>.Empty : commitDictionaries[commitIndex - 1];

                var expectedPuts = currentFiles.Keys
                                               .Except(previousFiles.Keys, FileInfoModule.Comparer)
                                               .Concat(previousFiles.Keys.Intersect(currentFiles.Keys, FileInfoModule.Comparer)
                                                                    .Where(file => previousFiles[file] != currentFiles[file]));

                var expectedDeletes = previousFiles.Keys.Except(currentFiles.Keys, FileInfoModule.Comparer);

                var actionDictionary = await Assert.That(filesOption)
                                                   .IsSome() ?? [];

                var actualPuts = actionDictionary.Find(GitAction.Put).IfNone(() => []);
                var actualDeletes = actionDictionary.Find(GitAction.Delete).IfNone(() => []);

                await Assert.That(actualPuts)
                            .IsEquivalentTo(expectedPuts, FileInfoModule.Comparer);

                await Assert.That(actualDeletes)
                            .IsEquivalentTo(expectedDeletes, FileInfoModule.Comparer);
            }
            finally
            {
                repositoryDirectory.DeleteIfExists();
            }
        }, iter: 20);

        static async Task<ImmutableArray<CommitId>> writeCommits(IEnumerable<ImmutableDictionary<FileInfo, string>> commits, DirectoryInfo repositoryDirectory, string authorName, string authorEmail, CancellationToken cancellationToken)
        {
            var commitIds = new List<CommitId>();

            async ValueTask writeFile(KeyValuePair<FileInfo, string> fileContents)
            {
                var (file, contents) = fileContents;

                file.Directory!.Create();

                await File.WriteAllTextAsync(file.FullName, contents, cancellationToken);
            }

            void deleteFile(FileInfo file)
            {
                if (file.Exists)
                {
                    file.Delete();
                }
            }

            await commits.Select((files, index) => (files, index))
                         .IterTask(async tuple =>
                         {
                             var (files, index) = tuple;

                             if (index == 0)
                             {
                                 await files.IterTask(writeFile, cancellationToken);
                             }
                             else
                             {
                                 var previousFiles = commits.ElementAt(index - 1);

                                 var filesToDelete = previousFiles.Keys.Except(files.Keys, FileInfoModule.Comparer);
                                 filesToDelete.Iter(deleteFile);

                                 await files.IterTask(writeFile, cancellationToken);
                             }

                             var message = $"Commit {index}";
                             var signatureDate = DateTimeOffset.UtcNow;
                             var commit = index == 0
                                         ? common.GitModule.InitializeRepository(repositoryDirectory, message, authorName, authorEmail, signatureDate)
                                         : common.GitModule.CommitChanges(repositoryDirectory, message, authorName, authorEmail, signatureDate);
                             commitIds.Add(CommitId.From(commit));
                         }, cancellationToken);

            return [.. commitIds];
        }
    }

    private sealed record Fixture
    {
        public required GetCurrentCommitId GetCurrentCommitId { get; init; }
        public required ServiceDirectory ServiceDirectory { get; init; }

        public ListServiceDirectoryFilesModifiedByCurrentCommit Resolve()
        {
            var services = new ServiceCollection();

            services.AddSingleton(GetCurrentCommitId)
                    .AddSingleton(ServiceDirectory);

            using var provider = services.BuildServiceProvider();

            return GitModule.ResolveListServiceDirectoryFilesModifiedByCurrentCommit(provider);
        }

        public static Gen<Fixture> Generate() =>
            from directory in Generator.DirectoryInfo
            from commitId in from commitIdString in Gen.String
                             select CommitId.From(commitIdString)
                                            .ToOption()
            select new Fixture
            {
                GetCurrentCommitId = () => commitId,
                ServiceDirectory = ServiceDirectory.FromDirectoryInfo(directory)
            };
    }
}