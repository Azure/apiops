using common;
using common.tests;
using CsCheck;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace publisher.tests;

internal sealed class PutWorkspaceApiTests
{
    public static CancellationToken CancellationToken => TestContext.Current?.Execution.CancellationToken ?? CancellationToken.None;

    [Test]
    public async Task Putting_a_root_api_without_changing_the_revision_number_creates_no_release()
    {
        var gen = from resourceKey in Generator.GenerateResourceKey(WorkspaceApiResource.Instance)
                  where ApiRevisionModule.IsRootName(resourceKey.Name)
                  from revision in Gen.Int[1, 100]
                  from currentDto in from currentDisplayName in Gen.String
                                     select new JsonObject
                                     {
                                         ["properties"] = new JsonObject
                                         {
                                             ["apiRevision"] = $"{revision}",
                                             ["displayName"] = currentDisplayName
                                         }
                                     }
                  let putResources = new ConcurrentQueue<(ResourceKey Key, JsonObject Dto)>([(resourceKey, currentDto)])
                  from newDto in from newDisplayName in Gen.String
                                 select new JsonObject
                                 {
                                     ["properties"] = new JsonObject
                                     {
                                         ["apiRevision"] = $"{revision}",
                                         ["displayName"] = newDisplayName
                                     }
                                 }
                  from fixture in Fixture.Generate()
                  select (resourceKey, newDto, putResources, fixture with
                  {
                      DoesResourceExistInApim = async (key, _) =>
                      {
                          await ValueTask.CompletedTask;
                          return putResources.Any(tuple => tuple.Key == key);
                      },
                      GetResourceDtoFromApim = async (resource, name, parentChain, _) =>
                      {
                          await ValueTask.CompletedTask;

                          var key = new ResourceKey
                          {
                              Name = name,
                              Parents = parentChain,
                              Resource = resource
                          };

                          return putResources.First(tuple => tuple.Key == key)
                                             .Dto;
                      },
                      PutResourceInApim = async (resource, name, dto, parentChain, _) =>
                      {
                          await ValueTask.CompletedTask;

                          var key = new ResourceKey
                          {
                              Name = name,
                              Parents = parentChain,
                              Resource = resource
                          };

                          putResources.Enqueue((key, dto));
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (resourceKey, newDto, putResources, fixture) = tuple;
            var putWorkspaceApi = fixture.Resolve();

            // Act
            await putWorkspaceApi(resourceKey.Name, resourceKey.Parents, newDto, CancellationToken);

            // Assert that we put the API with its root name.
            await Assert.That(putResources)
                        .Contains((resourceKey, newDto));

            // Assert that we did not create an API release.
            await Assert.That(putResources)
                        .DoesNotContain(tuple => tuple.Key.Resource is WorkspaceApiReleaseResource);
        });
    }

    [Test]
    public async Task Changing_the_current_revision_number_clones_the_current_revision_and_creates_a_release()
    {
        var gen = from resourceKey in Generator.GenerateResourceKey(WorkspaceApiResource.Instance)
                  where ApiRevisionModule.IsRootName(resourceKey.Name)
                  from newRevision in Gen.Int[1, 100]
                  let newDto = new JsonObject
                  {
                      ["properties"] = new JsonObject
                      {
                          ["apiRevision"] = $"{newRevision}",
                          ["isCurrent"] = true
                      }
                  }
                  from currentDto in from currentRevision in Gen.Int[1, 100]
                                     where currentRevision != newRevision
                                     select new JsonObject
                                     {
                                         ["properties"] = new JsonObject
                                         {
                                             ["apiRevision"] = $"{currentRevision}",
                                             ["isCurrent"] = true
                                         }
                                     }
                  let putResources = new ConcurrentQueue<(ResourceKey Key, JsonObject Dto)>([(resourceKey, currentDto)])
                  let deletedResources = new ConcurrentQueue<ResourceKey>()
                  from fixture in Fixture.Generate()
                  select (resourceKey, newRevision, newDto, putResources, deletedResources, fixture with
                  {
                      DoesResourceExistInApim = async (key, _) =>
                      {
                          await ValueTask.CompletedTask;
                          return putResources.Any(tuple => tuple.Key == key);
                      },
                      GetResourceDtoFromApim = async (resource, name, parentChain, _) =>
                      {
                          await ValueTask.CompletedTask;

                          var key = new ResourceKey
                          {
                              Name = name,
                              Parents = parentChain,
                              Resource = resource
                          };

                          return putResources.First(tuple => tuple.Key == key
                                                             && deletedResources.Contains(tuple.Key) is false)
                                             .Dto;
                      },
                      PutResourceInApim = async (resource, name, dto, parentChain, _) =>
                      {
                          await ValueTask.CompletedTask;

                          var key = new ResourceKey
                          {
                              Name = name,
                              Parents = parentChain,
                              Resource = resource
                          };

                          putResources.Enqueue((key, dto));
                      },
                      DeleteResourceFromApim = async (key, _, _, _) =>
                      {
                          await ValueTask.CompletedTask;
                          deletedResources.Enqueue(key);
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (resourceKey, newRevision, newDto, putResources, deletedResources, fixture) = tuple;
            var putWorkspaceApi = fixture.Resolve();

            // Act
            await putWorkspaceApi(resourceKey.Name, resourceKey.Parents, newDto, CancellationToken);

            // Assert that we put the API with its revisioned name
            var newRevisionedName = ApiRevisionModule.Combine(resourceKey.Name, newRevision);
            var (_, cloneDto) = await Assert.That(putResources)
                                            .Contains(tuple => tuple.Key.Name == newRevisionedName
                                                               && tuple.Key.Resource is WorkspaceApiResource
                                                               && tuple.Key.Parents == resourceKey.Parents);

            // Assert that the revisioned name PUT passed the correct revision and source API ID
            var clonePropertiesResult = from propertiesJson in cloneDto.GetJsonObjectProperty("properties")
                                        from revision in propertiesJson.GetStringProperty("apiRevision")
                                        from apiId in propertiesJson.GetStringProperty("sourceApiId")
                                        select (revision, apiId);

            var (clonedRevision, clonedApiId) = await Assert.That(clonePropertiesResult)
                                                            .IsSuccess();

            await Assert.That(clonedRevision)
                        .IsEqualTo($"{newRevision}");

            var expectedApiId = resourceKey.Parents.Append(WorkspaceApiResource.Instance, resourceKey.Name).ToResourceId();

            await Assert.That(clonedApiId)
                        .IsEqualTo(expectedApiId);

            // Assert that we put the API again with its root name.
            await Assert.That(putResources)
                        .Contains((resourceKey, newDto));

            // Assert that we created an API release
            var (releaseKey, _) = await Assert.That(putResources)
                                              .Contains(tuple => tuple.Key.Resource is WorkspaceApiReleaseResource);

            // Assert that we cleaned up the API release
            await Assert.That(deletedResources)
                        .Contains(releaseKey);
        });
    }

    private sealed record Fixture
    {
        public required GetCurrentFileOperations GetCurrentFileOperations { get; init; }
        public required DoesResourceExistInApim DoesResourceExistInApim { get; init; }
        public required GetResourceDtoFromApim GetResourceDtoFromApim { get; init; }
        public required PutResourceInApim PutResourceInApim { get; init; }
        public required DeleteResourceFromApim DeleteResourceFromApim { get; init; }
        public required GetApiSpecificationFromFile GetApiSpecificationFromFile { get; init; }
        public required PutApiSpecificationInApim PutApiSpecificationInApim { get; init; }

        public PutWorkspaceApi Resolve()
        {
            var services = new ServiceCollection();

            services.AddSingleton(GetCurrentFileOperations)
                    .AddSingleton(DoesResourceExistInApim)
                    .AddSingleton(GetResourceDtoFromApim)
                    .AddSingleton(PutResourceInApim)
                    .AddSingleton(DeleteResourceFromApim)
                    .AddSingleton(GetApiSpecificationFromFile)
                    .AddSingleton(PutApiSpecificationInApim)
                    .AddNullLogger();

            using var provider = services.BuildServiceProvider();

            return ResourceModule.ResolvePutWorkspaceApi(provider);
        }

        public static Gen<Fixture> Generate() =>
            from exists in Generator.GeneratePredicate<ResourceKey>()
            from dto in Generator.JsonObject
            from specificationContents in Gen.Select(Generator.ApiSpecification, Generator.BinaryData)
                                             .OptionOf()
            select new Fixture
            {
                GetCurrentFileOperations = () => Common.NoOpFileOperations,
                DoesResourceExistInApim = async (resourceKey, _) =>
                {
                    await ValueTask.CompletedTask;
                    return exists(resourceKey);
                },
                GetResourceDtoFromApim = async (_, _, _, _) =>
                {
                    await ValueTask.CompletedTask;
                    return dto;
                },
                PutResourceInApim = async (_, _, _, _, _) =>
                {
                    await ValueTask.CompletedTask;
                },
                DeleteResourceFromApim = async (_, _, _, _) =>
                {
                    await ValueTask.CompletedTask;
                },
                GetApiSpecificationFromFile = async (_, _, _) =>
                {
                    await ValueTask.CompletedTask;
                    return specificationContents;
                },
                PutApiSpecificationInApim = async (_, _, _, _) =>
                {
                    await ValueTask.CompletedTask;
                }
            };
    }
}

internal sealed class DeleteWorkspaceApiTests
{
    public static CancellationToken CancellationToken => TestContext.Current?.Execution.CancellationToken ?? CancellationToken.None;

    [Test]
    public async Task Skip_if_name_is_revisioned_and_the_revision_is_current()
    {
        var gen = from baseKey in Generator.GenerateResourceKey(WorkspaceApiResource.Instance)
                  where ApiRevisionModule.IsRootName(baseKey.Name)
                  from revision in Gen.Int[1, 100]
                  let name = ApiRevisionModule.Combine(baseKey.Name, revision)
                  let dto = new JsonObject
                  {
                      ["properties"] = new JsonObject
                      {
                          ["apiRevision"] = $"{revision}",
                          ["isCurrent"] = true
                      }
                  }
                  from fixture in Fixture.Generate()
                  let deletedResources = new ConcurrentBag<ResourceKey>()
                  select (name, baseKey.Parents, deletedResources, fixture with
                  {
                      DeleteResourceFromApim = async (resourceKey, _, _, _) =>
                      {
                          await ValueTask.CompletedTask;
                          deletedResources.Add(resourceKey);
                      },
                      GetDto = async (_, _, _, _) =>
                      {
                          await ValueTask.CompletedTask;
                          return dto;
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (name, parents, deletedResources, fixture) = tuple;
            var deleteWorkspaceApi = fixture.Resolve();

            // Act
            await deleteWorkspaceApi(name, parents, CancellationToken);

            // Assert that API was not deleted
            await Assert.That(deletedResources)
                        .DoesNotContain(resourceKey => resourceKey.Name == name && resourceKey.Parents == parents);
        });
    }

    [Test]
    public async Task Delete_if_name_is_revisioned_and_the_revision_is_not_current()
    {
        var gen = from baseKey in Generator.GenerateResourceKey(WorkspaceApiResource.Instance)
                  where ApiRevisionModule.IsRootName(baseKey.Name)
                  from revision in Gen.Int[1, 100]
                  let name = ApiRevisionModule.Combine(baseKey.Name, revision)
                  from currentRevision in Gen.Int[1, 100]
                  where currentRevision != revision
                  let dto = new JsonObject
                  {
                      ["properties"] = new JsonObject
                      {
                          ["apiRevision"] = $"{currentRevision}",
                          ["isCurrent"] = false
                      }
                  }
                  from fixture in Fixture.Generate()
                  let deletedResources = new ConcurrentBag<ResourceKey>()
                  select (name, baseKey.Parents, deletedResources, fixture with
                  {
                      DeleteResourceFromApim = async (resourceKey, _, _, _) =>
                      {
                          await ValueTask.CompletedTask;
                          deletedResources.Add(resourceKey);
                      },
                      GetDto = async (_, _, _, _) =>
                      {
                          await ValueTask.CompletedTask;
                          return dto;
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (name, parents, deletedResources, fixture) = tuple;
            var deleteWorkspaceApi = fixture.Resolve();

            // Act
            await deleteWorkspaceApi(name, parents, CancellationToken);

            // Assert that API was deleted
            await Assert.That(deletedResources)
                        .Contains(resourceKey => resourceKey.Name == name && resourceKey.Parents == parents);
        });
    }

    [Test]
    public async Task Delete_if_name_is_not_revisioned()
    {
        var gen = from resourceKey in Generator.GenerateResourceKey(WorkspaceApiResource.Instance)
                  where ApiRevisionModule.IsRootName(resourceKey.Name)
                  from fixture in Fixture.Generate()
                  let deletedResources = new ConcurrentBag<ResourceKey>()
                  select (resourceKey, deletedResources, fixture with
                  {
                      DeleteResourceFromApim = async (key, _, _, _) =>
                      {
                          await ValueTask.CompletedTask;
                          deletedResources.Add(key);
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (resourceKey, deletedResources, fixture) = tuple;
            var deleteWorkspaceApi = fixture.Resolve();

            // Act
            await deleteWorkspaceApi(resourceKey.Name, resourceKey.Parents, CancellationToken);

            // Assert that API was deleted
            await Assert.That(deletedResources)
                        .Contains(key => key.Name == resourceKey.Name && key.Parents == resourceKey.Parents);
        });
    }

    private sealed record Fixture
    {
        public required DoesResourceExistInApim DoesResourceExistInApim { get; init; }
        public required DeleteResourceFromApim DeleteResourceFromApim { get; init; }
        public required GetDto GetDto { get; init; }

        public DeleteWorkspaceApi Resolve()
        {
            var services = new ServiceCollection();

            services.AddSingleton(DoesResourceExistInApim)
                    .AddSingleton(DeleteResourceFromApim)
                    .AddSingleton(GetDto)
                    .AddNullLogger();

            using var provider = services.BuildServiceProvider();

            return ResourceModule.ResolveDeleteWorkspaceApi(provider);
        }

        public static Gen<Fixture> Generate() =>
            from exists in Generator.GeneratePredicate<ResourceKey>()
            from dtoOption in Generator.JsonObject.OptionOf()
            select new Fixture
            {
                DoesResourceExistInApim = async (resourceKey, _) =>
                {
                    await ValueTask.CompletedTask;
                    return exists(resourceKey);
                },
                DeleteResourceFromApim = async (_, _, _, _) =>
                {
                    await ValueTask.CompletedTask;
                },
                GetDto = async (_, _, _, _) =>
                {
                    await ValueTask.CompletedTask;
                    return dtoOption;
                }
            };
    }
}