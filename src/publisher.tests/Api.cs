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

internal sealed class PutApiTests
{
    public static CancellationToken CancellationToken => TestContext.Current?.Execution.CancellationToken ?? CancellationToken.None;

    [Test]
    public async Task Putting_a_root_api_without_changing_the_revision_number_creates_no_release()
    {
        var gen = from name in Generator.ResourceName
                  where ApiRevisionModule.IsRootName(name)
                  from revision in Gen.Int[1, 100]
                  let resourceKey = new ResourceKey
                  {
                      Name = name,
                      Parents = ParentChain.Empty,
                      Resource = ApiResource.Instance
                  }
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
                  select (name, newDto, putResources, fixture with
                  {
                      DoesResourceExistInApim = async (resourceKey, _) =>
                      {
                          await ValueTask.CompletedTask;
                          return putResources.Any(tuple => tuple.Key == resourceKey);
                      },
                      GetResourceDtoFromApim = async (resource, name, parentChain, _) =>
                      {
                          await ValueTask.CompletedTask;

                          var resourceKey = new ResourceKey
                          {
                              Name = name,
                              Parents = parentChain,
                              Resource = resource
                          };

                          return putResources.First(tuple => tuple.Key == resourceKey)
                                             .Dto;
                      },
                      PutResourceInApim = async (resource, name, dto, parentChain, _) =>
                      {
                          await ValueTask.CompletedTask;

                          var resourceKey = new ResourceKey
                          {
                              Name = name,
                              Parents = parentChain,
                              Resource = resource
                          };

                          putResources.Enqueue((resourceKey, dto));
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (name, newDto, putResources, fixture) = tuple;
            var putApi = fixture.Resolve();

            // Act
            await putApi(name, newDto, CancellationToken);

            // Assert that we put the API with its root name.
            var resourceKey = new ResourceKey
            {
                Name = name,
                Parents = ParentChain.Empty,
                Resource = ApiResource.Instance
            };

            await Assert.That(putResources)
                        .Contains((resourceKey, newDto));

            // Assert that we did not create an API release.
            await Assert.That(putResources)
                        .DoesNotContain(tuple => tuple.Key.Resource is ApiReleaseResource);
        });
    }

    [Test]
    public async Task Changing_the_current_revision_number_clones_the_current_revision_and_creates_a_release()
    {
        var gen = from name in Generator.ResourceName
                  where ApiRevisionModule.IsRootName(name)
                  from newRevision in Gen.Int[1, 100]
                  let newDto = new JsonObject
                  {
                      ["properties"] = new JsonObject
                      {
                          ["apiRevision"] = $"{newRevision}",
                          ["isCurrent"] = true
                      }
                  }
                  let resourceKey = new ResourceKey
                  {
                      Name = name,
                      Parents = ParentChain.Empty,
                      Resource = ApiResource.Instance
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
                  select (name, newRevision, newDto, putResources, deletedResources, fixture with
                  {
                      DoesResourceExistInApim = async (resourceKey, _) =>
                      {
                          await ValueTask.CompletedTask;
                          return putResources.Any(tuple => tuple.Key == resourceKey);
                      },
                      GetResourceDtoFromApim = async (resource, name, parentChain, _) =>
                      {
                          await ValueTask.CompletedTask;

                          var resourceKey = new ResourceKey
                          {
                              Name = name,
                              Parents = parentChain,
                              Resource = resource
                          };

                          return putResources.First(tuple => tuple.Key == resourceKey
                                                             && deletedResources.Contains(tuple.Key) is false)
                                             .Dto;
                      },
                      PutResourceInApim = async (resource, name, dto, parentChain, _) =>
                      {
                          await ValueTask.CompletedTask;

                          var resourceKey = new ResourceKey
                          {
                              Name = name,
                              Parents = parentChain,
                              Resource = resource
                          };

                          putResources.Enqueue((resourceKey, dto));
                      },
                      DeleteResourceFromApim = async (resourceKey, _, _, _) =>
                      {
                          await ValueTask.CompletedTask;

                          deletedResources.Enqueue(resourceKey);
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (name, newRevision, newDto, putResources, deletedResources, fixture) = tuple;
            var putApi = fixture.Resolve();

            // Act
            await putApi(name, newDto, CancellationToken);

            // Assert that we put the API with its revisioned name
            var newRevisionedName = ApiRevisionModule.Combine(name, newRevision);
            var (_, cloneDto) = await Assert.That(putResources)
                                            .Contains(tuple => tuple.Key.Name == newRevisionedName);

            // Assert that the revisioned name PUT passed the correct revision and source API ID
            var clonePropertiesResult = from propertiesJson in cloneDto.GetJsonObjectProperty("properties")
                                        from revision in propertiesJson.GetStringProperty("apiRevision")
                                        from apiId in propertiesJson.GetStringProperty("sourceApiId")
                                        select (revision, apiId);

            var (clonedRevision, clonedApiId) = await Assert.That(clonePropertiesResult)
                                                            .IsSuccess();

            await Assert.That(clonedRevision)
                        .IsEqualTo($"{newRevision}");

            await Assert.That(clonedApiId)
                        .IsEqualTo($"/apis/{name}");

            // Assert that we put the API again with its root name.
            var resourceKey = new ResourceKey
            {
                Name = name,
                Parents = ParentChain.Empty,
                Resource = ApiResource.Instance
            };

            await Assert.That(putResources)
                        .Contains((resourceKey, newDto));

            // Assert that we created an API release
            var (releaseKey, _) = await Assert.That(putResources)
                                              .Contains(tuple => tuple.Key.Resource is ApiReleaseResource);

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

        public PutApi Resolve()
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

            return ResourceModule.ResolvePutApi(provider);
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

internal sealed class DeleteApiTests
{
    public static CancellationToken CancellationToken => TestContext.Current?.Execution.CancellationToken ?? CancellationToken.None;

    [Test]
    public async Task Skip_if_name_is_revisioned_and_the_revision_is_current()
    {
        var gen = from revision in Gen.Int[1, 100]
                  from name in from rootName in Generator.ResourceName
                               select ApiRevisionModule.Combine(rootName, revision)
                  let dto = new JsonObject
                  {
                      ["properties"] = new JsonObject
                      {
                          ["apiRevision"] = $"{revision}",
                          ["isCurrent"] = true
                      }
                  }
                  from fixture in Fixture.Generate()
                  let deletedResources = new ConcurrentBag<ResourceName>()
                  select (name, deletedResources, fixture with
                  {
                      DoesResourceExistInApim = async (resourceKey, _) =>
                      {
                          await ValueTask.CompletedTask;
                          return true;
                      },
                      DeleteResourceFromApim = async (resourceKey, _, _, _) =>
                      {
                          await ValueTask.CompletedTask;
                          deletedResources.Add(resourceKey.Name);
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
            var (name, deletedResources, fixture) = tuple;
            var deleteApi = fixture.Resolve();

            // Act
            await deleteApi(name, CancellationToken);

            // Assert that API was not deleted
            await Assert.That(deletedResources)
                        .DoesNotContain(name);
        });
    }

    [Test]
    public async Task Delete_if_name_is_revisioned_and_the_revision_is_not_current()
    {
        var gen = from revision in Gen.Int[1, 100]
                  from name in from rootName in Generator.ResourceName
                               select ApiRevisionModule.Combine(rootName, revision)
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
                  let deletedResources = new ConcurrentBag<ResourceName>()
                  select (name, deletedResources, fixture with
                  {
                      DeleteResourceFromApim = async (resourceKey, _, _, _) =>
                      {
                          await ValueTask.CompletedTask;
                          deletedResources.Add(resourceKey.Name);
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
            var (name, deletedResources, fixture) = tuple;
            var deleteApi = fixture.Resolve();

            // Act
            await deleteApi(name, CancellationToken);

            // Assert that API was deleted
            await Assert.That(deletedResources)
                        .Contains(name);
        });
    }

    [Test]
    public async Task Delete_if_name_is_not_revisioned()
    {
        var gen = from name in Generator.ResourceName
                  where ApiRevisionModule.IsRootName(name)
                  from fixture in Fixture.Generate()
                  let deletedResources = new ConcurrentBag<ResourceName>()
                  select (name, deletedResources, fixture with
                  {
                      DeleteResourceFromApim = async (resourceKey, _, _, _) =>
                      {
                          await ValueTask.CompletedTask;
                          deletedResources.Add(resourceKey.Name);
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (name, deletedResources, fixture) = tuple;
            var deleteApi = fixture.Resolve();

            // Act
            await deleteApi(name, CancellationToken);

            // Assert that API was deleted
            await Assert.That(deletedResources)
                        .Contains(name);
        });
    }

    private sealed record Fixture
    {
        public required DoesResourceExistInApim DoesResourceExistInApim { get; init; }
        public required DeleteResourceFromApim DeleteResourceFromApim { get; init; }
        public required GetDto GetDto { get; init; }

        public DeleteApi Resolve()
        {
            var services = new ServiceCollection();

            services.AddSingleton(DoesResourceExistInApim)
                    .AddSingleton(DeleteResourceFromApim)
                    .AddSingleton(GetDto)
                    .AddNullLogger();

            using var provider = services.BuildServiceProvider();

            return ResourceModule.ResolveDeleteApi(provider);
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