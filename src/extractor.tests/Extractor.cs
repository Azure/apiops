using common;
using common.tests;
using CsCheck;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace extractor.tests;

internal sealed class RunExtractorTests
{
    private static CancellationToken CancellationToken =>
        TestContext.Current?.Execution.CancellationToken ?? CancellationToken.None;

    [Test]
    public async Task Unsupported_resources_are_not_extracted()
    {
        var gen = from isResourceSupported in Generator.GeneratePredicate<IResource>()
                  from fixture in Fixture.Generate()
                  select (isResourceSupported, fixture with
                  {
                      IsResourceSupportedInApim = async (resource, cancellationToken) =>
                      {
                          await ValueTask.CompletedTask;
                          return isResourceSupported(resource);
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (isResourceSupported, fixture) = tuple;
            var writtenResources = ImmutableArray<IResource>.Empty;

            fixture = fixture with
            {
                WriteResource = async (resourceKey, dtoOption, cancellationToken) =>
                {
                    await ValueTask.CompletedTask;
                    ImmutableInterlocked.Update(ref writtenResources, array => array.Add(resourceKey.Resource));
                }
            };

            var runExtractor = fixture.Resolve();

            // Act
            await runExtractor(CancellationToken);

            // Assert
            await Assert.That(writtenResources)
                        .All(isResourceSupported);
        });
    }

    [Test]
    public async Task Descendants_of_unsupported_resources_are_not_extracted()
    {
        var gen = from isResourceSupported in Generator.GeneratePredicate<IResource>()
                  from fixture in Fixture.Generate()
                  select (isResourceSupported, fixture with
                  {
                      IsResourceSupportedInApim = async (resource, cancellationToken) =>
                      {
                          await ValueTask.CompletedTask;
                          return isResourceSupported(resource);
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (isResourceSupported, fixture) = tuple;
            var writtenResources = ImmutableArray<IResource>.Empty;

            fixture = fixture with
            {
                WriteResource = async (resourceKey, dtoOption, cancellationToken) =>
                {
                    await ValueTask.CompletedTask;
                    ImmutableInterlocked.Update(ref writtenResources, array => array.Add(resourceKey.Resource));
                }
            };

            var runExtractor = fixture.Resolve();

            // Act
            await runExtractor(CancellationToken);

            // Assert
            var ancestorsOfWrittenResources = writtenResources.SelectMany(resource => resource.GetTraversalPredecessorHierarchy());

            await Assert.That(ancestorsOfWrittenResources)
                        .All(isResourceSupported);
        });
    }

    [Test]
    public async Task Filtered_out_resources_are_not_extracted()
    {
        var gen = from shouldExtract in Generator.GeneratePredicate<ResourceKey>()
                  from fixture in Fixture.Generate()
                  select (shouldExtract, fixture with
                  {
                      ShouldExtract = async (key, cancellationToken) =>
                      {
                          await ValueTask.CompletedTask;
                          return shouldExtract(key);
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (shouldExtract, fixture) = tuple;
            var writtenResources = ImmutableArray<ResourceKey>.Empty;

            fixture = fixture with
            {
                WriteResource = async (resourceKey, dtoOption, cancellationToken) =>
                {
                    await ValueTask.CompletedTask;
                    ImmutableInterlocked.Update(ref writtenResources, array => array.Add(resourceKey));
                }
            };

            var runExtractor = fixture.Resolve();

            // Act
            await runExtractor(CancellationToken);

            // Assert
            await Assert.That(writtenResources)
                        .All(shouldExtract);
        });
    }

    [Test]
    public async Task Written_dtos_match_apim_dtos()
    {
        var gen = from apimDtos in Generator.ResourceDtos
                  from fixture in Fixture.Generate()
                  select (apimDtos, fixture with
                  {
                      ListResourceDtosFromApim = (resource, parents, cancellationToken) =>
                      {
                          return apimDtos.Where(kvp => kvp.Key.Resource == resource && kvp.Key.Parents == parents)
                                         .Choose(kvp => from dto in kvp.Value
                                                        select (kvp.Key.Name, dto))
                                         .ToAsyncEnumerable();
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (apimDtos, fixture) = tuple;
            var writtenDtos = ImmutableDictionary<ResourceKey, JsonObject>.Empty;

            fixture = fixture with
            {
                WriteResource = async (resourceKey, dtoOption, cancellationToken) =>
                {
                    await ValueTask.CompletedTask;
                    dtoOption.Iter(dto => ImmutableInterlocked.AddOrUpdate(ref writtenDtos, resourceKey, dto, (_, _) => dto));
                }
            };

            var runExtractor = fixture.Resolve();

            // Act
            await runExtractor(CancellationToken);

            // Assert
            var apimJsonObjects = apimDtos.Choose(kvp => from json in kvp.Value
                                                         select KeyValuePair.Create(kvp.Key, json))
                                          .ToImmutableHashSet();

            await Assert.That(writtenDtos).All(apimJsonObjects.Contains);
        });
    }

    [Test]
    public async Task Only_api_releases_of_current_apis_get_extracted()
    {
        var gen = from apimDtos in Generator.ResourceDtos
                      // Ensure we have at least one API release whose parent API is not the current revision
                  where apimDtos.Keys.Any(key => key.Resource is ApiReleaseResource or WorkspaceApiReleaseResource
                                                 && key.Parents.Any(parent => (parent.Resource is ApiResource or WorkspaceApiResource)
                                                                               && ApiRevisionModule.IsRootName(parent.Name) is false))
                  from fixture in Fixture.Generate()
                  select fixture with
                  {
                      ListResourceDtosFromApim = (resource, parents, cancellationToken) =>
                      {
                          return apimDtos.Where(kvp => kvp.Key.Resource == resource && kvp.Key.Parents == parents)
                                           .Choose(kvp => from dto in kvp.Value
                                                          select (kvp.Key.Name, dto))
                                           .ToAsyncEnumerable();
                      },
                      IsResourceSupportedInApim = async (resource, cancellationToken) =>
                      {
                          await ValueTask.CompletedTask;
                          return true;
                      },
                      ShouldExtract = async (key, cancellationToken) =>
                      {
                          await ValueTask.CompletedTask;
                          return true;
                      }
                  };

        await gen.SampleAsync(async fixture =>
        {
            // Arrange
            var writtenResourceKeys = ImmutableArray<ResourceKey>.Empty;

            fixture = fixture with
            {
                WriteResource = async (resourceKey, dtoOption, cancellationToken) =>
                {
                    await ValueTask.CompletedTask;
                    ImmutableInterlocked.Update(ref writtenResourceKeys, array => array.Add(resourceKey));
                }
            };

            var runExtractor = fixture.Resolve();

            // Act
            await runExtractor(CancellationToken);

            // Assert
            var writtenReleases = writtenResourceKeys.Where(key => key.Resource is ApiReleaseResource or WorkspaceApiReleaseResource);

            await Assert.That(writtenReleases)
                        .All(noRevisionedParentApi);
        });

        // All parent APIs are the current revision
        static bool noRevisionedParentApi(ResourceKey key) =>
            key.Parents
               .All(parent => parent.Resource is not (ApiResource or WorkspaceApiResource)
                              || ApiRevisionModule.IsRootName(parent.Name));
    }

    private sealed record Fixture
    {
        public required ResourceGraph Graph { get; init; }
        public required IsResourceSupportedInApim IsResourceSupportedInApim { get; init; }
        public required ListResourceDtosFromApim ListResourceDtosFromApim { get; init; }
        public required ListResourceNamesFromApim ListResourceNamesFromApim { get; init; }
        public required ShouldExtract ShouldExtract { get; init; }
        public required WriteResource WriteResource { get; init; }

        public RunExtractor Resolve()
        {
            var services = new ServiceCollection();

            services.AddSingleton<ResourceGraph>(Graph)
                    .AddSingleton<IsResourceSupportedInApim>(IsResourceSupportedInApim)
                    .AddSingleton<ListResourceNamesFromApim>(ListResourceNamesFromApim)
                    .AddSingleton<ListResourceDtosFromApim>(ListResourceDtosFromApim)
                    .AddSingleton<ShouldExtract>(ShouldExtract)
                    .AddSingleton<WriteResource>(WriteResource)
                    .AddTestActivitySource()
                    .AddNullLogger();

            using var provider = services.BuildServiceProvider();

            return ExtractorModule.ResolveRunExtractor(provider);
        }

        public static Gen<Fixture> Generate() =>
            from resourceDtos in Generator.ResourceDtos
            from isResourceSupported in Generator.GeneratePredicate<IResource>()
            select new Fixture
            {
                Graph = ResourceGraph.From(resourceDtos.Keys.Select(key => key.Resource), CancellationToken.None),
                IsResourceSupportedInApim = async (key, cancellationToken) =>
                {
                    await ValueTask.CompletedTask;
                    return isResourceSupported(key);
                },
                ListResourceNamesFromApim = (resource, parents, cancellationToken) =>
                {
                    return resourceDtos.Keys
                                       .Where(key => key.Resource == resource && key.Parents == parents)
                                       .Select(key => key.Name)
                                       .ToAsyncEnumerable();
                },
                ListResourceDtosFromApim = (resource, parents, cancellationToken) =>
                {
                    return resourceDtos.Where(kvp => kvp.Key.Resource == resource && kvp.Key.Parents == parents)
                                       .Choose(kvp => from dto in kvp.Value
                                                      select (kvp.Key.Name, dto))
                                       .ToAsyncEnumerable();
                },
                ShouldExtract = (_, _) => ValueTask.FromResult(true),
                WriteResource = (_, _, _) => ValueTask.CompletedTask
            };
    }
}

internal sealed class ShouldExtractTests
{
    private static CancellationToken CancellationToken =>
        TestContext.Current?.Execution.CancellationToken ?? CancellationToken.None;

    [Test]
    public async Task Resources_in_configuration_are_extracted()
    {
        var gen = from fixture in Fixture.Generate()
                  from resourceKey in Generator.ResourceKey
                  select (resourceKey, fixture with
                  {
                      ResourceIsInConfiguration = async (_, _) =>
                      {
                          await ValueTask.CompletedTask;
                          return true;
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (resourceKey, fixture) = tuple;
            var shouldExtract = fixture.Resolve();

            // Act
            var result = await shouldExtract(resourceKey, CancellationToken);

            // Assert
            await Assert.That(result).IsTrue();
        });
    }

    [Test]
    public async Task Resources_not_in_configuration_are_not_extracted()
    {
        var gen = from fixture in Fixture.Generate()
                  from resourceKey in Generator.ResourceKey
                  select (resourceKey, fixture with
                  {
                      ResourceIsInConfiguration = async (_, _) =>
                      {
                          await ValueTask.CompletedTask;
                          return false;
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (resourceKey, fixture) = tuple;
            var shouldExtract = fixture.Resolve();

            // Act
            var result = await shouldExtract(resourceKey, CancellationToken);

            // Assert
            await Assert.That(result).IsFalse();
        });
    }

    [Test]
    public async Task Unknown_resources_are_extracted()
    {
        var gen = from fixture in Fixture.Generate()
                  from resourceKey in Generator.ResourceKey
                  select (resourceKey, fixture with
                  {
                      ResourceIsInConfiguration = async (_, _) =>
                      {
                          await ValueTask.CompletedTask;
                          return Option.None;
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (resourceKey, fixture) = tuple;
            var shouldExtract = fixture.Resolve();

            // Act
            var result = await shouldExtract(resourceKey, CancellationToken);

            // Assert
            await Assert.That(result).IsTrue();
        });
    }

    [Test]
    public async Task Known_exceptions_are_skipped()
    {
        var gen = from fixture in Fixture.Generate()
                  from resourceKey in
                      Gen.OneOf(from resourceKey in Generator.GenerateResourceKey(SubscriptionResource.Instance)
                                select resourceKey with
                                {
                                    Name = SubscriptionResource.Master
                                },
                                from resourceKey in Generator.GenerateResourceKey(GroupResource.Instance)
                                from name in Gen.OneOfConst(GroupResource.Administrators, GroupResource.Developers, GroupResource.Guests)
                                select resourceKey with
                                {
                                    Name = name
                                },
                                from resourceKey in Generator.GenerateResourceKey(WorkspaceGroupResource.Instance)
                                from name in Gen.OneOfConst(GroupResource.Administrators, GroupResource.Developers, GroupResource.Guests)
                                select resourceKey with
                                {
                                    Name = name
                                })
                  select (resourceKey, fixture with
                  {
                      ResourceIsInConfiguration = async (_, _) =>
                      {
                          await ValueTask.CompletedTask;
                          return true;
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (resourceKey, fixture) = tuple;
            var shouldExtract = fixture.Resolve();

            // Act
            var result = await shouldExtract(resourceKey, CancellationToken);

            // Assert
            await Assert.That(result).IsFalse();
        });
    }

    private sealed record Fixture
    {
        public required ResourceIsInConfiguration ResourceIsInConfiguration { get; init; }

        public ShouldExtract Resolve()
        {
            var services = new ServiceCollection();

            services.AddSingleton<ResourceIsInConfiguration>(ResourceIsInConfiguration)
                    .AddSingleton<ActivitySource>(provider => new ActivitySource("extractor.tests"))
                    .AddSingleton<Microsoft.Extensions.Logging.ILogger>(NullLogger.Instance);

            using var provider = services.BuildServiceProvider();

            return ExtractorModule.ResolveShouldExtract(provider);
        }

        public static Gen<Fixture> Generate() =>
            Gen.Const(new Fixture
            {
                ResourceIsInConfiguration = async (resourceKey, cancellationToken) =>
                {
                    await ValueTask.CompletedTask;
                    return (resourceKey.GetHashCode() % 3) switch
                    {
                        0 => true,
                        1 => false,
                        _ => Option.None
                    };
                }
            });
    }
}

internal sealed class WriteResourceTests
{
    private static CancellationToken CancellationToken =>
        TestContext.Current?.Execution.CancellationToken ?? CancellationToken.None;

    [Test]
    public async Task Information_file_with_dto_is_written()
    {
        var gen = from resourceKey in Generator.GenerateResourceKey(resource => resource is IResourceWithInformationFile)
                  let dto = new JsonObject
                  {
                      ["id"] = resourceKey.ToString()
                  }
                  from fixture in Fixture.Generate()
                  select (resourceKey, dto, fixture);

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (resourceKey, dto, fixture) = tuple;
            var writtenDto = new JsonObject();
            fixture = fixture with
            {
                WriteInformationFile = async (resource, name, dto, parents, cancellationToken) =>
                {
                    await ValueTask.CompletedTask;
                    writtenDto = dto;
                }
            };
            var writeResource = fixture.Resolve();

            // Act
            await writeResource(resourceKey, dto, CancellationToken);

            // Assert
            await Assert.That(writtenDto).IsEqualTo(dto);
        });
    }

    [Test]
    public async Task Information_file_without_dto_is_not_written()
    {
        var gen = from resourceKey in Generator.GenerateResourceKey(resource => resource is IResourceWithInformationFile)
                  let dtoOption = Option<JsonObject>.None()
                  from fixture in Fixture.Generate()
                  select (resourceKey, dtoOption, fixture);

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (resourceKey, dtoOption, fixture) = tuple;
            var wasWritten = false;
            fixture = fixture with
            {
                WriteInformationFile = async (resource, name, dto, parents, cancellationToken) =>
                {
                    await ValueTask.CompletedTask;
                    wasWritten = true;
                }
            };
            var writeResource = fixture.Resolve();

            // Act
            await writeResource(resourceKey, dtoOption, CancellationToken);

            // Assert
            await Assert.That(wasWritten).IsFalse();
        });
    }
    [Test]
    public async Task Policy_file_with_dto_is_written()
    {
        var gen = from resourceKey in Generator.GenerateResourceKey(resource => resource is IPolicyResource)
                  let dto = new JsonObject
                  {
                      ["id"] = resourceKey.ToString()
                  }
                  from fixture in Fixture.Generate()
                  select (resourceKey, dto, fixture);

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (resourceKey, dto, fixture) = tuple;
            var writtenDto = new JsonObject();
            fixture = fixture with
            {
                WritePolicyFile = async (resource, name, dto, parents, cancellationToken) =>
                {
                    await ValueTask.CompletedTask;
                    writtenDto = dto;
                }
            };
            var writeResource = fixture.Resolve();

            // Act
            await writeResource(resourceKey, dto, CancellationToken);

            // Assert
            await Assert.That(writtenDto).IsEqualTo(dto);
        });
    }

    [Test]
    public async Task Policy_file_without_dto_is_not_written()
    {
        var gen = from resourceKey in Generator.GenerateResourceKey(resource => resource is IPolicyResource)
                  let dtoOption = Option<JsonObject>.None()
                  from fixture in Fixture.Generate()
                  select (resourceKey, dtoOption, fixture);

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (resourceKey, dtoOption, fixture) = tuple;
            var wasWritten = false;
            fixture = fixture with
            {
                WritePolicyFile = async (resource, name, dto, parents, cancellationToken) =>
                {
                    await ValueTask.CompletedTask;
                    wasWritten = true;
                }
            };
            var writeResource = fixture.Resolve();

            // Act
            await writeResource(resourceKey, dtoOption, CancellationToken);

            // Assert
            await Assert.That(wasWritten).IsFalse();
        });
    }

    [Test]
    public async Task Existing_api_specification_is_written()
    {
        var gen = from resourceKey in Generator.GenerateResourceKey(resource => resource is ApiResource or WorkspaceApiResource)
                  let dto = new JsonObject
                  {
                      ["id"] = resourceKey.ToString()
                  }
                  let specificationContents = BinaryData.FromObjectAsJson(dto)
                  from fixture in Fixture.Generate()
                  select (resourceKey, dto, specificationContents, fixture);

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (resourceKey, dto, specificationContents, fixture) = tuple;
            var writtenSpecification = string.Empty;
            fixture = fixture with
            {
                GetApiSpecificationFromApim = async (resourceKey, dto, cancellationToken) =>
                {
                    await ValueTask.CompletedTask;
                    return Option.Some((ApiSpecification.OpenApi.GraphQl.Instance as ApiSpecification, specificationContents));
                },
                WriteApiSpecificationFile = async (resourceKey, specification, contents, cancellationToken) =>
                {
                    await ValueTask.CompletedTask;
                    writtenSpecification = contents.ToString();
                }
            };
            var writeResource = fixture.Resolve();

            // Act
            await writeResource(resourceKey, dto, CancellationToken);

            // Assert
            await Assert.That(writtenSpecification).IsEquivalentTo(specificationContents.ToString());
        });
    }

    [Test]
    public async Task Missing_api_specification_is_not_written()
    {
        var gen = from resourceKey in Generator.GenerateResourceKey(resource => resource is ApiResource or WorkspaceApiResource)
                  let dtoOption = Option.Some(new JsonObject())
                  from fixture in Fixture.Generate()
                  select (resourceKey, dtoOption, fixture);

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (resourceKey, dtoOption, fixture) = tuple;
            var wasSpecificationWritten = false;
            fixture = fixture with
            {
                GetApiSpecificationFromApim = async (resourceKey, dto, cancellationToken) =>
                {
                    await ValueTask.CompletedTask;
                    return Option.None;
                },
                WriteApiSpecificationFile = async (resourceKey, specification, contents, cancellationToken) =>
                {
                    await ValueTask.CompletedTask;
                    wasSpecificationWritten = true;
                }
            };
            var writeResource = fixture.Resolve();

            // Act
            await writeResource(resourceKey, dtoOption, CancellationToken);

            // Assert
            await Assert.That(wasSpecificationWritten).IsFalse();
        });
    }

    private sealed record Fixture
    {
        public required WriteInformationFile WriteInformationFile { get; init; }
        public required WritePolicyFile WritePolicyFile { get; init; }
        public required GetApiSpecificationFromApim GetApiSpecificationFromApim { get; init; }
        public required WriteApiSpecificationFile WriteApiSpecificationFile { get; init; }

        public WriteResource Resolve()
        {
            var services = new ServiceCollection();

            services.AddSingleton<WriteInformationFile>(WriteInformationFile)
                    .AddSingleton<WritePolicyFile>(WritePolicyFile)
                    .AddSingleton<GetApiSpecificationFromApim>(GetApiSpecificationFromApim)
                    .AddSingleton<WriteApiSpecificationFile>(WriteApiSpecificationFile)
                    .AddTestActivitySource()
                    .AddNullLogger();

            using var provider = services.BuildServiceProvider();

            return ExtractorModule.ResolveWriteResource(provider);
        }

        public static Gen<Fixture> Generate() =>
            Gen.Const(new Fixture
            {
                WriteInformationFile = (_, _, _, _, _) => ValueTask.CompletedTask,
                WritePolicyFile = (_, _, _, _, _) => ValueTask.CompletedTask,
                GetApiSpecificationFromApim = async (_, _, _) =>
                {
                    await ValueTask.CompletedTask;
                    return Option.None;
                },
                WriteApiSpecificationFile = (_, _, _, _) => ValueTask.CompletedTask
            });
    }
}