using common;
using common.tests;
using CsCheck;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace publisher.tests;

internal sealed class ListResourcesToProcessTests
{
    public static CancellationToken CancellationToken => TestContext.Current?.Execution.CancellationToken ?? CancellationToken.None;

    [Test]
    public async Task Lists_local_resources_if_no_commit_id_is_passed()
    {
        var gen = from fileResourceKeys in GenerateFileResourceKeys()
                  from fixture in Fixture.Generate()
                  select (fileResourceKeys, fixture with
                  {
                      ParseResourceFile = async (file, _, _) =>
                      {
                          await ValueTask.CompletedTask;
                          return fileResourceKeys.Find(file)
                                                 .IfNone(() => Option.None);
                      },
                      CommitIdWasPassed = () => false,
                      GetLocalFileOperations = () => new FileOperations
                      {
                          EnumerateServiceDirectoryFiles = () => [.. fileResourceKeys.Keys],
                          GetSubDirectories = _ => Option.None,
                          ReadFile = async (_, _) =>
                          {
                              await ValueTask.CompletedTask;
                              return Option.None;
                          }
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (fileResourceKeys, fixture) = tuple;
            var listResourcesToProcess = fixture.Resolve();

            // Act
            var resources = await listResourcesToProcess(CancellationToken);

            // Assert that keys associated with local files are returned
            var localResources = fileResourceKeys.Choose(kvp => kvp.Value);
            await Assert.That(resources)
                        .SetEquals(localResources);
        });
    }

    private static Gen<ImmutableDictionary<FileInfo, Option<ResourceKey>>> GenerateFileResourceKeys() =>
        Gen.Select(Generator.FileInfo, Generator.ResourceKey.OptionOf())
           .Array
           .Select(array => array.DistinctBy(x => x.Item1)
                                 .ToImmutableDictionary(x => x.Item1, x => x.Item2));

    [Test]
    public async Task Lists_resources_associated_with_the_commit_id()
    {
        var gen = from fileResourceKeys in GenerateFileResourceKeys()
                  from currentCommitResourceKeys in Generator.SubSetOf(fileResourceKeys)
                                                             .Select(kvps => kvps.ToImmutableDictionary())
                  from previousCommitResourceKeys in Generator.SubSetOf(fileResourceKeys)
                                                              .Select(kvps => kvps.ToImmutableDictionary())
                  from gitActionFiles in from puts in Generator.SubSetOf([.. currentCommitResourceKeys.Keys])
                                         from deletes in Generator.SubSetOf([.. previousCommitResourceKeys.Keys.Except(puts)])
                                         select ImmutableDictionary.CreateRange([KeyValuePair.Create(GitAction.Put, puts),
                                                                                 KeyValuePair.Create(GitAction.Delete, deletes)])
                  from fixture in Fixture.Generate()
                  select (currentCommitResourceKeys, previousCommitResourceKeys, gitActionFiles, fixture with
                  {
                      ParseResourceFile = async (file, readFile, _) =>
                      {
                          var contentsOption = await readFile(file, _);

                          return contentsOption.Bind(contents => contents.ToString() switch
                          {
                              "CURRENT" => currentCommitResourceKeys.Find(file)
                                                                    .IfNone(() => Option.None),
                              "PREVIOUS" => previousCommitResourceKeys.Find(file)
                                                                      .IfNone(() => Option.None),
                              _ => Option.None
                          });
                      },
                      CommitIdWasPassed = () => true,
                      GetCurrentCommitFileOperations = () => Common.NoOpFileOperations with
                      {
                          ReadFile = async (_, _) =>
                          {
                              await ValueTask.CompletedTask;
                              return BinaryData.FromString("CURRENT");
                          }
                      },
                      GetPreviousCommitFileOperations = () => Common.NoOpFileOperations with
                      {
                          ReadFile = async (_, _) =>
                          {
                              await ValueTask.CompletedTask;
                              return BinaryData.FromString("PREVIOUS");
                          }
                      },
                      ListServiceDirectoryFilesModifiedByCurrentCommit = () => gitActionFiles
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (currentCommitResourceKeys, previousCommitResourceKeys, gitActionFiles, fixture) = tuple;
            var listResourcesToProcess = fixture.Resolve();

            // Act
            var resources = await listResourcesToProcess(CancellationToken);

            // Assert that put resource keys come from the current commit
            var putResourceKeys = gitActionFiles.Find(GitAction.Put)
                                                .IfNone(() => [])
                                                .Choose(file => currentCommitResourceKeys
                                                                .Find(file)
                                                                .IfNone(() => Option.None))
                                                .ToImmutableArray();

            await Assert.That(putResourceKeys)
                        .All(resources.Contains);

            // Assert that delete resource keys come from the previous commit
            var deleteResourceKeys = gitActionFiles.Find(GitAction.Delete)
                                                   .IfNone(() => [])
                                                   .Choose(file => previousCommitResourceKeys
                                                                   .Find(file)
                                                                   .IfNone(() => Option.None))
                                                   .ToImmutableArray();

            await Assert.That(deleteResourceKeys)
                        .All(resources.Contains);

            // Assert that no other resource keys are returned
            await Assert.That(resources)
                        .SetEquals([.. putResourceKeys, .. deleteResourceKeys]);
        });
    }

    private sealed record Fixture
    {
        public required ParseResourceFile ParseResourceFile { get; init; }
        public required CommitIdWasPassed CommitIdWasPassed { get; init; }
        public required ListServiceDirectoryFilesModifiedByCurrentCommit ListServiceDirectoryFilesModifiedByCurrentCommit { get; init; }
        public required GetCurrentCommitFileOperations GetCurrentCommitFileOperations { get; init; }
        public required GetPreviousCommitFileOperations GetPreviousCommitFileOperations { get; init; }
        public required GetLocalFileOperations GetLocalFileOperations { get; init; }

        public ListResourcesToProcess Resolve()
        {
            var services = new ServiceCollection();

            services.AddSingleton(ParseResourceFile)
                    .AddSingleton(CommitIdWasPassed)
                    .AddSingleton(ListServiceDirectoryFilesModifiedByCurrentCommit)
                    .AddSingleton(GetCurrentCommitFileOperations)
                    .AddSingleton(GetPreviousCommitFileOperations)
                    .AddSingleton(GetLocalFileOperations);

            using var provider = services.BuildServiceProvider();

            return ResourceModule.ResolveListResourcesToProcess(provider);
        }

        public static Gen<Fixture> Generate() =>
            from parsedResource in Generator.ResourceKey.OptionOf()
            from wasCommitPassed in Gen.Bool
            from files in Gen.Const(ImmutableDictionary<GitAction, ImmutableHashSet<FileInfo>>.Empty).OptionOf()
            select new Fixture
            {
                ParseResourceFile = async (_, _, _) =>
                {
                    await ValueTask.CompletedTask;
                    return parsedResource;
                },
                CommitIdWasPassed = () => wasCommitPassed,
                ListServiceDirectoryFilesModifiedByCurrentCommit = () => files,
                GetCurrentCommitFileOperations = () => Common.NoOpFileOperations,
                GetPreviousCommitFileOperations = () => Common.NoOpFileOperations,
                GetLocalFileOperations = () => Common.NoOpFileOperations
            };
    }
}

internal sealed class IsResourceInFileSystemTests
{
    public static CancellationToken CancellationToken => TestContext.Current?.Execution.CancellationToken ?? CancellationToken.None;

    [Test]
    public async Task Returns_true_if_resource_meets_criteria()
    {
        var gen = from resourceKeyOptions in ResourceKeyOptions.Generate()
                  from fixture in Fixture.Generate()
                  select (resourceKeyOptions, fixture with
                  {
                      GetInformationFileDto = async (_, _, _, _, _, _) =>
                      {
                          await ValueTask.CompletedTask;
                          return resourceKeyOptions.InformationFileContentsOption;
                      },
                      GetPolicyFileContents = async (_, _, _, _, _) =>
                      {
                          await ValueTask.CompletedTask;
                          return resourceKeyOptions.PolicyContentsOption;
                      },
                      GetApiSpecificationFromFile = async (_, _, _) =>
                      {
                          await ValueTask.CompletedTask;
                          return resourceKeyOptions.SpecificationOption;
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (resourceKeyOptions, fixture) = tuple;
            var isResourceInFileSystem = fixture.Resolve();

            // Act
            var result = await isResourceInFileSystem(resourceKeyOptions.ResourceKey, CancellationToken);

            // Assert that result matches the conditions
            var resource = resourceKeyOptions.ResourceKey.Resource;
            var expected = (resource is IResourceWithInformationFile && resourceKeyOptions.InformationFileContentsOption.IsSome)
                           || (resource is IPolicyResource && resourceKeyOptions.PolicyContentsOption.IsSome)
                           || (resource is ApiResource or WorkspaceApiResource && resourceKeyOptions.SpecificationOption.IsSome);

            await Assert.That(result)
                        .IsEqualTo(expected);
        });
    }

    [Test]
    public async Task File_operations_rely_on_whether_commit_id_was_passed()
    {
        var gen = from resourceKeyOptions in ResourceKeyOptions.Generate()
                  let resourceKey = resourceKeyOptions.ResourceKey
                  from wasCommitPassed in Gen.Bool
                  let operationSources = new ConcurrentQueue<string>()
                  from fixture in Fixture.Generate()
                  let dummyFile = new FileInfo("dummy.txt")
                  select (resourceKey, wasCommitPassed, operationSources, fixture with
                  {
                      CommitIdWasPassed = () => wasCommitPassed,
                      GetCurrentCommitFileOperations = () => Common.NoOpFileOperations with
                      {
                          ReadFile = async (_, _) =>
                          {
                              await ValueTask.CompletedTask;
                              operationSources.Enqueue(nameof(GetCurrentCommitFileOperations));
                              return BinaryData.FromString("COMMIT");
                          }
                      },
                      GetLocalFileOperations = () => Common.NoOpFileOperations with
                      {
                          ReadFile = async (_, _) =>
                          {
                              await ValueTask.CompletedTask;
                              operationSources.Enqueue(nameof(GetLocalFileOperations));
                              return BinaryData.FromString("LOCAL");
                          }
                      },
                      GetInformationFileDto = async (_, _, _, readFile, _, cancellationToken) =>
                      {
                          await readFile(dummyFile, cancellationToken);

                          return resourceKeyOptions.InformationFileContentsOption;
                      },
                      GetPolicyFileContents = async (_, _, _, readFile, cancellationToken) =>
                      {
                          await readFile(dummyFile, cancellationToken);

                          return resourceKeyOptions.PolicyContentsOption;
                      },
                      GetApiSpecificationFromFile = async (_, readFile, cancellationToken) =>
                      {
                          await readFile(dummyFile, cancellationToken);

                          return resourceKeyOptions.SpecificationOption;
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (resourceKey, wasCommitPassed, operationSources, fixture) = tuple;
            var isResourceInFileSystem = fixture.Resolve();

            // Act
            var result = await isResourceInFileSystem(resourceKey, CancellationToken);

            // Assert that the correct file operations were used
            var expectedSource = wasCommitPassed
                                 ? nameof(GetCurrentCommitFileOperations)
                                 : nameof(GetLocalFileOperations);

            await Assert.That(operationSources)
                        .IsEmpty()
                        .Or
                        .All()
                        .Satisfy(source => source.IsEqualTo(expectedSource));
        });

    }

    private sealed record Fixture
    {
        public required CommitIdWasPassed CommitIdWasPassed { get; init; }
        public required GetCurrentCommitFileOperations GetCurrentCommitFileOperations { get; init; }
        public required GetLocalFileOperations GetLocalFileOperations { get; init; }
        public required GetInformationFileDto GetInformationFileDto { get; init; }
        public required GetPolicyFileContents GetPolicyFileContents { get; init; }
        public required GetApiSpecificationFromFile GetApiSpecificationFromFile { get; init; }

        public IsResourceInFileSystem Resolve()
        {
            var services = new ServiceCollection();

            services.AddSingleton(CommitIdWasPassed)
                    .AddSingleton(GetCurrentCommitFileOperations)
                    .AddSingleton(GetLocalFileOperations)
                    .AddSingleton(GetInformationFileDto)
                    .AddSingleton(GetPolicyFileContents)
                    .AddSingleton(GetApiSpecificationFromFile);

            using var provider = services.BuildServiceProvider();

            return ResourceModule.ResolveIsResourceInFileSystem(provider);
        }

        public static Gen<Fixture> Generate() =>
            from wasCommitPassed in Gen.Bool
            from parsedResource in Generator.ResourceKey.OptionOf()
            from files in Gen.Const(ImmutableDictionary<GitAction, ImmutableHashSet<FileInfo>>.Empty).OptionOf()
            select new Fixture
            {
                CommitIdWasPassed = () => wasCommitPassed,
                GetCurrentCommitFileOperations = () => Common.NoOpFileOperations,
                GetLocalFileOperations = () => Common.NoOpFileOperations,
                GetInformationFileDto = async (_, _, _, _, _, _) =>
                {
                    await ValueTask.CompletedTask;
                    return Option.None;
                },
                GetPolicyFileContents = async (_, _, _, _, _) =>
                {
                    await ValueTask.CompletedTask;
                    return Option.None;
                },
                GetApiSpecificationFromFile = async (_, _, _) =>
                {
                    await ValueTask.CompletedTask;
                    return Option.None;
                }
            };
    }

    private sealed record ResourceKeyOptions
    {
        public required ResourceKey ResourceKey { get; init; }
        public required Option<JsonObject> InformationFileContentsOption { get; init; }
        public required Option<BinaryData> PolicyContentsOption { get; init; }
        public required Option<(ApiSpecification, BinaryData)> SpecificationOption { get; init; }

        public static Gen<ResourceKeyOptions> Generate() =>
            from resourceKey in Generator.ResourceKey
            from informationFileContentsOption in
                resourceKey.Resource is IResourceWithInformationFile
                ? Gen.Const(new JsonObject()).OptionOf()
                : Gen.Const(Option<JsonObject>.None())
            from policyContentsOption in
                resourceKey.Resource is IPolicyResource
                ? Gen.String.Select(BinaryData.FromString).OptionOf()
                : Gen.Const(Option<BinaryData>.None())
            from specificationOption in
                resourceKey.Resource is ApiResource or WorkspaceApiResource
                ? Gen.Select(Generator.ApiSpecification, Generator.BinaryData).OptionOf()
                : Gen.Const(Option<(ApiSpecification, BinaryData)>.None())
            select new ResourceKeyOptions
            {
                ResourceKey = resourceKey,
                InformationFileContentsOption = informationFileContentsOption,
                PolicyContentsOption = policyContentsOption,
                SpecificationOption = specificationOption
            };
    }
}

internal sealed class PutResourceTests
{
    public static CancellationToken CancellationToken => TestContext.Current?.Execution.CancellationToken ?? CancellationToken.None;

    [Test]
    public async Task Non_dto_resources_are_not_put()
    {
        var gen = from resourceKey in Generator.GenerateResourceKey(resource => resource is not IResourceWithDto)
                  from dtoOption in Generator.JsonObject.OptionOf()
                  let putCalls = new ConcurrentBag<byte>()
                  from fixture in Fixture.Generate()
                  select (resourceKey, putCalls, fixture with
                  {
                      IsDryRun = () => false,
                      GetDto = async (_, _, _, _) =>
                      {
                          await ValueTask.CompletedTask;
                          return dtoOption;
                      },
                      PutApi = async (_, _, _) =>
                      {
                          await ValueTask.CompletedTask;
                          putCalls.Add(0);
                      },
                      PutWorkspaceApi = async (_, _, _, _) =>
                      {
                          await ValueTask.CompletedTask;
                          putCalls.Add(0);
                      },
                      PutResourceInApim = async (_, _, _, _, _) =>
                      {
                          await ValueTask.CompletedTask;
                          putCalls.Add(0);
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (resourceKey, putCalls, fixture) = tuple;
            var putResource = fixture.Resolve();

            // Act
            await putResource(resourceKey, CancellationToken);

            // Assert that no put methods were called
            await Assert.That(putCalls)
                        .IsEmpty();
        });
    }

    [Test]
    public async Task Dto_resources_without_a_dto_are_not_put()
    {
        var gen = from resourceKey in Generator.GenerateResourceKey(resource => resource is IResourceWithDto)
                  let putCalls = new ConcurrentBag<byte>()
                  from fixture in Fixture.Generate()
                  select (resourceKey, putCalls, fixture with
                  {
                      IsDryRun = () => false,
                      GetDto = async (_, _, _, _) =>
                      {
                          await ValueTask.CompletedTask;
                          return Option.None;
                      },
                      PutApi = async (_, _, _) =>
                      {
                          await ValueTask.CompletedTask;
                          putCalls.Add(0);
                      },
                      PutWorkspaceApi = async (_, _, _, _) =>
                      {
                          await ValueTask.CompletedTask;
                          putCalls.Add(0);
                      },
                      PutResourceInApim = async (_, _, _, _, _) =>
                      {
                          await ValueTask.CompletedTask;
                          putCalls.Add(0);
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (resourceKey, putCalls, fixture) = tuple;
            var putResource = fixture.Resolve();

            // Act
            await putResource(resourceKey, CancellationToken);

            // Assert that no put methods were called
            await Assert.That(putCalls)
                        .IsEmpty();
        });
    }

    [Test]
    public async Task Dto_resources_with_dtos_are_put()
    {
        var gen = from resourceKey in Generator.GenerateResourceKey(resource => resource is IResourceWithDto)
                  from dto in Generator.JsonObject
                  let putApis = new ConcurrentBag<(ResourceName Name, JsonObject Dto)>()
                  let putWorkspaceApis = new ConcurrentBag<(ResourceName Name, ParentChain ParentChain, JsonObject Dto)>()
                  let putOthers = new ConcurrentBag<(ResourceKey Key, JsonObject Dto)>()
                  from fixture in Fixture.Generate()
                  select (resourceKey, dto, putApis, putWorkspaceApis, putOthers, fixture with
                  {
                      IsDryRun = () => false,
                      GetDto = async (_, _, _, _) =>
                      {
                          await ValueTask.CompletedTask;
                          return dto;
                      },
                      PutApi = async (name, dto, _) =>
                      {
                          await ValueTask.CompletedTask;
                          putApis.Add((name, dto));
                      },
                      PutWorkspaceApi = async (name, parentChain, dto, _) =>
                      {
                          await ValueTask.CompletedTask;
                          putWorkspaceApis.Add((name, parentChain, dto));
                      },
                      PutResourceInApim = async (resource, name, dto, parents, _) =>
                      {
                          await ValueTask.CompletedTask;
                          var key = ResourceKey.From(resource, name, parents);
                          putOthers.Add((key, dto));
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (resourceKey, dto, putApis, putWorkspaceApis, putOthers, fixture) = tuple;
            var putResource = fixture.Resolve();

            // Act
            await putResource(resourceKey, CancellationToken);

            // Assert that DTO was put
            switch (resourceKey.Resource)
            {
                case ApiResource:
                    await Assert.That(putApis)
                                .Contains((resourceKey.Name, dto));
                    break;
                case WorkspaceApiResource:
                    await Assert.That(putWorkspaceApis)
                                .Contains((resourceKey.Name, resourceKey.Parents, dto));
                    break;
                default:
                    await Assert.That(putOthers)
                                .Contains((resourceKey, dto));
                    break;
            }
        });
    }

    [Test]
    public async Task Resources_are_not_put_in_dry_run()
    {
        var gen = from resourceKey in Generator.GenerateResourceKey(resource => resource is IResourceWithDto)
                  from dto in Generator.JsonObject
                  let putCalls = new ConcurrentBag<byte>()
                  from fixture in Fixture.Generate()
                  select (resourceKey, putCalls, fixture with
                  {
                      IsDryRun = () => true,
                      GetDto = async (_, _, _, _) =>
                      {
                          await ValueTask.CompletedTask;
                          return dto;
                      },
                      PutApi = async (_, _, _) =>
                      {
                          await ValueTask.CompletedTask;
                          putCalls.Add(0);
                      },
                      PutWorkspaceApi = async (_, _, _, _) =>
                      {
                          await ValueTask.CompletedTask;
                          putCalls.Add(0);
                      },
                      PutResourceInApim = async (_, _, _, _, _) =>
                      {
                          await ValueTask.CompletedTask;
                          putCalls.Add(0);
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (resourceKey, putCalls, fixture) = tuple;
            var putResource = fixture.Resolve();

            // Act
            await putResource(resourceKey, CancellationToken);

            // Assert that no put methods were called
            await Assert.That(putCalls)
                        .IsEmpty();
        });
    }

    private sealed record Fixture
    {
        public required IsDryRun IsDryRun { get; init; }
        public required GetDto GetDto { get; init; }
        public required PutApi PutApi { get; init; }
        public required PutWorkspaceApi PutWorkspaceApi { get; init; }
        public required PutResourceInApim PutResourceInApim { get; init; }

        public PutResource Resolve()
        {
            var services = new ServiceCollection();

            services.AddSingleton(IsDryRun)
                    .AddSingleton(GetDto)
                    .AddSingleton(PutApi)
                    .AddSingleton(PutWorkspaceApi)
                    .AddSingleton(PutResourceInApim)
                    .AddTestActivitySource()
                    .AddNullLogger();

            using var provider = services.BuildServiceProvider();

            return ResourceModule.ResolvePutResource(provider);
        }

        public static Gen<Fixture> Generate() =>
            from dtoOption in Generator.JsonObject.OptionOf()
            from isDryRun in Gen.Bool
            select new Fixture
            {
                IsDryRun = () => isDryRun,
                GetDto = async (_, _, _, _) =>
                {
                    await ValueTask.CompletedTask;
                    return dtoOption;
                },
                PutApi = async (_, _, _) =>
                {
                    await ValueTask.CompletedTask;
                },
                PutWorkspaceApi = async (_, _, _, _) =>
                {
                    await ValueTask.CompletedTask;
                },
                PutResourceInApim = async (_, _, _, _, _) =>
                {
                    await ValueTask.CompletedTask;
                }
            };
    }
}


internal sealed class DeleteResourceTests
{
    public static CancellationToken CancellationToken => TestContext.Current?.Execution.CancellationToken ?? CancellationToken.None;

    [Test]
    public async Task Proper_delegate_is_called()
    {
        var gen = from resourceKey in Generator.ResourceKey
                  let deletedApis = new ConcurrentBag<ResourceName>()
                  let deletedWorkspaceApis = new ConcurrentBag<ResourceKey>()
                  let deletedOthers = new ConcurrentBag<ResourceKey>()
                  from fixture in Fixture.Generate()
                  select (resourceKey, deletedApis, deletedWorkspaceApis, deletedOthers, fixture with
                  {
                      IsDryRun = () => false,
                      DeleteApi = async (name, _) =>
                      {
                          await ValueTask.CompletedTask;
                          deletedApis.Add(name);
                      },
                      DeleteWorkspaceApi = async (name, parentChain, _) =>
                      {
                          await ValueTask.CompletedTask;
                          var resourceKey = ResourceKey.From(WorkspaceApiResource.Instance, name, parentChain);
                          deletedWorkspaceApis.Add(resourceKey);
                      },
                      DeleteResourceFromApim = async (resourceKey, _, _, _) =>
                      {
                          await ValueTask.CompletedTask;
                          deletedOthers.Add(resourceKey);
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (resourceKey, deletedApis, deletedWorkspaceApis, deletedOthers, fixture) = tuple;
            var deleteResource = fixture.Resolve();

            // Act
            await deleteResource(resourceKey, CancellationToken);

            // Assert that proper delegate was called
            switch (resourceKey.Resource)
            {
                case ApiResource:
                    await Assert.That(deletedApis)
                                .Contains(resourceKey.Name);
                    break;
                case WorkspaceApiResource:
                    await Assert.That(deletedWorkspaceApis)
                                .Contains(resourceKey);
                    break;
                default:
                    await Assert.That(deletedOthers)
                                .Contains(resourceKey);
                    break;
            }
        });
    }

    [Test]
    public async Task Resources_are_not_deleted_in_dry_run()
    {
        var gen = from resourceKey in Generator.ResourceKey
                  let deleteCalls = new ConcurrentBag<byte>()
                  from fixture in Fixture.Generate()
                  select (resourceKey, deleteCalls, fixture with
                  {
                      IsDryRun = () => true,
                      DeleteApi = async (_, _) =>
                      {
                          await ValueTask.CompletedTask;
                          deleteCalls.Add(0);
                      },
                      DeleteWorkspaceApi = async (_, _, _) =>
                      {
                          await ValueTask.CompletedTask;
                          deleteCalls.Add(0);
                      },
                      DeleteResourceFromApim = async (_, _, _, _) =>
                      {
                          await ValueTask.CompletedTask;
                          deleteCalls.Add(0);
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (resourceKey, deleteCalls, fixture) = tuple;
            var deleteResource = fixture.Resolve();

            // Act
            await deleteResource(resourceKey, CancellationToken);

            // Assert that no delete methods were called
            await Assert.That(deleteCalls)
                        .IsEmpty();
        });
    }

    private sealed record Fixture
    {
        public required IsDryRun IsDryRun { get; init; }
        public required DeleteApi DeleteApi { get; init; }
        public required DeleteWorkspaceApi DeleteWorkspaceApi { get; init; }
        public required DeleteResourceFromApim DeleteResourceFromApim { get; init; }

        public DeleteResource Resolve()
        {
            var services = new ServiceCollection();

            services.AddSingleton(IsDryRun)
                    .AddSingleton(DeleteApi)
                    .AddSingleton(DeleteWorkspaceApi)
                    .AddSingleton(DeleteResourceFromApim)
                    .AddTestActivitySource()
                    .AddNullLogger();

            using var provider = services.BuildServiceProvider();

            return ResourceModule.ResolveDeleteResource(provider);
        }

        public static Gen<Fixture> Generate() =>
            from isDryRun in Gen.Bool
            select new Fixture
            {
                IsDryRun = () => isDryRun,
                DeleteApi = async (_, _) =>
                {
                    await ValueTask.CompletedTask;
                },
                DeleteWorkspaceApi = async (_, _, _) =>
                {
                    await ValueTask.CompletedTask;
                },
                DeleteResourceFromApim = async (_, _, _, _) =>
                {
                    await ValueTask.CompletedTask;
                }
            };
    }
}

internal sealed class GetDtoTests
{
    public static CancellationToken CancellationToken => TestContext.Current?.Execution.CancellationToken ?? CancellationToken.None;

    [Test]
    public async Task Dto_comes_from_correct_source()
    {
        var gen = from resourceKey in Generator.GenerateResourceKey(resource => resource is IResourceWithDto)
                  from policyFragmentDtoOption in GenerateDtoJsonObject(resourceKey).OptionOf()
                  from workspacePolicyFragmentDtoOption in GenerateDtoJsonObject(resourceKey).OptionOf()
                  from informationFileDtoOption in GenerateDtoJsonObject(resourceKey).OptionOf()
                  from policyContentsOption in Generator.BinaryData.OptionOf()
                  from fixture in Fixture.Generate()
                  select (resourceKey, policyFragmentDtoOption, workspacePolicyFragmentDtoOption, informationFileDtoOption, policyContentsOption, fixture with
                  {
                      GetInformationFileDto = async (_, _, _, _, _, _) =>
                      {
                          await ValueTask.CompletedTask;
                          return informationFileDtoOption;
                      },
                      GetPolicyFileContents = async (_, _, _, _, _) =>
                      {
                          await ValueTask.CompletedTask;
                          return policyContentsOption;
                      },
                      GetPolicyFragmentDto = async (_, _) =>
                      {
                          await ValueTask.CompletedTask;
                          return policyFragmentDtoOption;
                      },
                      GetWorkspacePolicyFragmentDto = async (_, _, _) =>
                      {
                          await ValueTask.CompletedTask;
                          return workspacePolicyFragmentDtoOption;
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (resourceKey, policyFragmentDtoOption, workspacePolicyFragmentDtoOption, informationFileDtoOption, policyContentsOption, fixture) = tuple;
            var getDto = fixture.Resolve();

            // Act
            var dtoOption = await getDto((IResourceWithDto)resourceKey.Resource, resourceKey.Name, resourceKey.Parents, CancellationToken);

            // Assert that DTO comes from expected source
            var expectedSome = resourceKey.Resource switch
            {
                PolicyFragmentResource => policyFragmentDtoOption.IsSome,
                WorkspacePolicyFragmentResource => workspacePolicyFragmentDtoOption.IsSome,
                IResourceWithInformationFile => informationFileDtoOption.IsSome,
                IPolicyResource => policyContentsOption.IsSome,
                _ => false
            };

            await Assert.That(dtoOption.IsSome)
                        .IsEqualTo(expectedSome);
        });
    }

    private static Gen<JsonObject> GenerateDtoJsonObject(ResourceKey resourceKey) =>
        Gen.Const(new JsonObject
        {
            ["properties"] = new JsonObject()
        });

    [Test]
    public async Task Configuration_overrides_dto()
    {
        var gen = from resourceKey in Generator.GenerateResourceKey(resource => resource is IResourceWithDto)
                  from policyFragmentDtoOption in GenerateDtoJsonObject(resourceKey).OptionOf()
                  from workspacePolicyFragmentDtoOption in GenerateDtoJsonObject(resourceKey).OptionOf()
                  from informationFileDtoOption in GenerateDtoJsonObject(resourceKey).OptionOf()
                  from policyContentsOption in Generator.BinaryData.OptionOf()
                  from configurationKeyValuePair in from key in Gen.String
                                                    from value in Gen.String
                                                    select KeyValuePair.Create(key, value)
                  from fixture in Fixture.Generate()
                  select (resourceKey, policyFragmentDtoOption, workspacePolicyFragmentDtoOption, informationFileDtoOption, policyContentsOption, configurationKeyValuePair, fixture with
                  {
                      GetInformationFileDto = async (_, _, _, _, _, _) =>
                      {
                          await ValueTask.CompletedTask;
                          return informationFileDtoOption;
                      },
                      GetPolicyFileContents = async (_, _, _, _, _) =>
                      {
                          await ValueTask.CompletedTask;
                          return policyContentsOption;
                      },
                      GetPolicyFragmentDto = async (_, _) =>
                      {
                          await ValueTask.CompletedTask;
                          return policyFragmentDtoOption;
                      },
                      GetWorkspacePolicyFragmentDto = async (_, _, _) =>
                      {
                          await ValueTask.CompletedTask;
                          return workspacePolicyFragmentDtoOption;
                      },
                      GetConfigurationOverride = async (_, _) =>
                      {
                          await ValueTask.CompletedTask;
                          return new JsonObject
                          {
                              [configurationKeyValuePair.Key] = configurationKeyValuePair.Value
                          };
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (resourceKey, policyFragmentDtoOption, workspacePolicyFragmentDtoOption, informationFileDtoOption, policyContentsOption, configurationKeyValuePair, fixture) = tuple;
            var getDto = fixture.Resolve();

            // Act
            var dtoOption = await getDto((IResourceWithDto)resourceKey.Resource, resourceKey.Name, resourceKey.Parents, CancellationToken);

            // Assert that DTO has been overridden if it exists
            await dtoOption.IterTask(async dto =>
            {
                var (key, value) = configurationKeyValuePair;

                var dtoKeyValuePairs = from kvp in dto
                                       select KeyValuePair.Create(kvp.Key, kvp.Value.ToString() ?? string.Empty);

                await Assert.That(dtoKeyValuePairs)
                            .Contains(configurationKeyValuePair);
            });
        });
    }

    [Test]
    public async Task Secret_named_values_return_None()
    {
        var gen = from resourceKey in Generator.GenerateResourceKey(resource => resource is NamedValueResource or WorkspaceNamedValueResource)
                  from dto in GenerateSecretNamedValueDto()
                  from fixture in Fixture.Generate()
                  select (resourceKey, fixture with
                  {
                      GetInformationFileDto = async (_, _, _, _, _, _) =>
                      {
                          await ValueTask.CompletedTask;
                          return dto;
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (resourceKey, fixture) = tuple;
            var getDto = fixture.Resolve();

            // Act
            var dtoOption = await getDto((IResourceWithDto)resourceKey.Resource, resourceKey.Name, resourceKey.Parents, CancellationToken);

            // Assert that DTO is None
            await Assert.That(dtoOption)
                        .IsNone();
        });
    }

    private static Gen<JsonObject> GenerateSecretNamedValueDto()
    {
        var setSecretIdentifierGen =
            Gen.OneOfConst((JsonObject jsonObject) => jsonObject.SetProperty("secretIdentifier", null, mutateOriginal: true),
                           (JsonObject jsonObject) => jsonObject.RemoveProperty("secretIdentifier", mutateOriginal: true));

        var setKeyVaultGen =
            Gen.OneOf(Gen.Const((JsonObject jsonObject) => jsonObject.SetProperty("keyVault", null, mutateOriginal: true)),
                      Gen.Const((JsonObject jsonObject) => jsonObject.RemoveProperty("keyVault", mutateOriginal: true)),
                      from setSecretIdentifier in setSecretIdentifierGen
                      select (Func<JsonObject, JsonObject>)(jsonObject =>
                      {
                          var keyVaultJson = jsonObject.GetJsonObjectProperty("keyVault")
                                                       .IfError(_ => []);

                          var withSecretIdentifier = setSecretIdentifier(keyVaultJson);

                          return jsonObject.SetProperty("keyVault", withSecretIdentifier, mutateOriginal: true);
                      }));

        var setValueGen =
            Gen.OneOfConst((JsonObject jsonObject) => jsonObject.SetProperty("value", null, mutateOriginal: true),
                           (JsonObject jsonObject) => jsonObject.RemoveProperty("value", mutateOriginal: true));

        var setPropertiesGen =
            from setValue in setValueGen
            from setKeyVault in setKeyVaultGen
            select (Func<JsonObject, JsonObject>)(jsonObject =>
            {
                var propertiesJson = jsonObject.GetJsonObjectProperty("properties")
                                               .IfError(_ => []);

                var withSecret = propertiesJson.SetProperty("secret", true, mutateOriginal: true);
                var withValue = setValue(withSecret);
                var withKeyVault = setKeyVault(withValue);

                return jsonObject.SetProperty("properties", withKeyVault, mutateOriginal: true);
            });

        return from setProperties in setPropertiesGen
               select setProperties([]);
    }

    private sealed record Fixture
    {
        public required CommitIdWasPassed CommitIdWasPassed { get; init; }
        public required GetCurrentCommitFileOperations GetCurrentCommitFileOperations { get; init; }
        public required GetLocalFileOperations GetLocalFileOperations { get; init; }
        public required GetInformationFileDto GetInformationFileDto { get; init; }
        public required GetPolicyFileContents GetPolicyFileContents { get; init; }
        public required GetPolicyFragmentDto GetPolicyFragmentDto { get; init; }
        public required GetWorkspacePolicyFragmentDto GetWorkspacePolicyFragmentDto { get; init; }
        public required GetConfigurationOverride GetConfigurationOverride { get; init; }

        public GetDto Resolve()
        {
            var services = new ServiceCollection();

            services.AddSingleton(CommitIdWasPassed)
                    .AddSingleton(GetCurrentCommitFileOperations)
                    .AddSingleton(GetLocalFileOperations)
                    .AddSingleton(GetInformationFileDto)
                    .AddSingleton(GetPolicyFileContents)
                    .AddSingleton(GetPolicyFragmentDto)
                    .AddSingleton(GetWorkspacePolicyFragmentDto)
                    .AddSingleton(GetConfigurationOverride)
                    .AddTestActivitySource()
                    .AddNullLogger();

            using var provider = services.BuildServiceProvider();

            return ResourceModule.ResolveGetDto(provider);
        }

        public static Gen<Fixture> Generate() =>
            from parsedResource in Generator.ResourceKey.OptionOf()
            from wasCommitPassed in Gen.Bool
            from files in Gen.Const(ImmutableDictionary<GitAction, ImmutableHashSet<FileInfo>>.Empty).OptionOf()
            select new Fixture
            {
                CommitIdWasPassed = () => wasCommitPassed,
                GetCurrentCommitFileOperations = () => Common.NoOpFileOperations,
                GetLocalFileOperations = () => Common.NoOpFileOperations,
                GetInformationFileDto = async (_, _, _, _, _, _) =>
                {
                    await ValueTask.CompletedTask;
                    return Option.None;
                },
                GetPolicyFileContents = async (_, _, _, _, _) =>
                {
                    await ValueTask.CompletedTask;
                    return Option.None;
                },
                GetPolicyFragmentDto = async (_, _) =>
                {
                    await ValueTask.CompletedTask;
                    return Option.None;
                },
                GetWorkspacePolicyFragmentDto = async (_, _, _) =>
                {
                    await ValueTask.CompletedTask;
                    return Option.None;
                },
                GetConfigurationOverride = async (_, _) =>
                {
                    await ValueTask.CompletedTask;
                    return Option.None;
                }
            };
    }
}