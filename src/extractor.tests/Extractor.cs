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
        var gen = from fixture in Fixture.Generate()
                  from isResourceSupported in Generator.GeneratePredicate<IResource>()
                  let writtenResources = new List<IResource>()
                  select (writtenResources, isResourceSupported, fixture with
                  {
                      IsResourceSupportedInApim = async (resource, cancellationToken) =>
                      {
                          await ValueTask.CompletedTask;
                          return isResourceSupported(resource);
                      },
                      WriteResource = async (resourceKey, dtoOption, cancellationToken) =>
                      {
                          await ValueTask.CompletedTask;
                          writtenResources.Add(resourceKey.Resource);
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (writtenResources, isResourceSupported, fixture) = tuple;
            var runExtractor = fixture.Resolve();

            // Act
            await runExtractor(CancellationToken);

            // Assert that all written resources are supported
            await Assert.That(writtenResources)
                        .All(isResourceSupported);
        });
    }

    [Test]
    public async Task Descendants_of_unsupported_resources_are_not_extracted()
    {
        var gen = from fixture in Fixture.Generate()
                  from isResourceSupported in Generator.GeneratePredicate<IResource>()
                  let writtenResources = new List<IResource>()
                  select (writtenResources, isResourceSupported, fixture with
                  {
                      IsResourceSupportedInApim = async (resource, cancellationToken) =>
                      {
                          await ValueTask.CompletedTask;
                          return isResourceSupported(resource);
                      },
                      WriteResource = async (resourceKey, dtoOption, cancellationToken) =>
                      {
                          await ValueTask.CompletedTask;
                          writtenResources.Add(resourceKey.Resource);
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (writtenResources, isResourceSupported, fixture) = tuple;
            var runExtractor = fixture.Resolve();

            // Act
            await runExtractor(CancellationToken);

            // Assert that all ancestors of written resources are supported
            var ancestorsOfWrittenResources = writtenResources.SelectMany(resource => resource.GetTraversalPredecessorHierarchy());

            await Assert.That(ancestorsOfWrittenResources)
                        .All(isResourceSupported);
        });
    }

    [Test]
    public async Task Filtered_out_resources_are_not_extracted()
    {
        var gen = from fixture in Fixture.Generate()
                  from shouldExtract in Generator.GeneratePredicate<ResourceKey>()
                  let writtenResourceKeys = new List<ResourceKey>()
                  select (writtenResourceKeys, shouldExtract, fixture with
                  {
                      ShouldExtract = async (key, _, cancellationToken) =>
                      {
                          await ValueTask.CompletedTask;
                          return shouldExtract(key);
                      },
                      WriteResource = async (resourceKey, dtoOption, cancellationToken) =>
                      {
                          await ValueTask.CompletedTask;
                          writtenResourceKeys.Add(resourceKey);
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (writtenResourceKeys, shouldExtract, fixture) = tuple;
            var runExtractor = fixture.Resolve();

            // Act
            await runExtractor(CancellationToken);

            // Assert that all written resources pass the filter
            await Assert.That(writtenResourceKeys)
                        .All(shouldExtract);
        });
    }

    [Test]
    public async Task Written_dtos_match_apim_dtos()
    {
        var gen = from fixture in Fixture.Generate()
                  from apimDtos in Generator.ResourceDtos
                  let writtenResources = new Dictionary<ResourceKey, JsonObject>()
                  select (apimDtos, writtenResources, fixture with
                  {
                      ListResourceDtosFromApim = (resource, parents, cancellationToken) =>
                      {
                          return apimDtos.Where(kvp => kvp.Key.Resource == resource && kvp.Key.Parents == parents)
                                         .Choose(kvp => from dto in kvp.Value
                                                        select (kvp.Key.Name, dto))
                                         .ToAsyncEnumerable();
                      },
                      WriteResource = async (resourceKey, dtoOption, cancellationToken) =>
                      {
                          await ValueTask.CompletedTask;
                          dtoOption.Iter(dto => writtenResources.Add(resourceKey, dto));
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (apimDtos, writtenResources, fixture) = tuple;
            var runExtractor = fixture.Resolve();

            // Act
            await runExtractor(CancellationToken);

            // Assert that all written dtos match apim dtos
            var apimJsonObjects = apimDtos.Choose(kvp => from json in kvp.Value
                                                         select KeyValuePair.Create(kvp.Key, json))
                                          .ToImmutableHashSet();

            await Assert.That(writtenResources)
                        .All(apimJsonObjects.Contains);
        });
    }

    [Test]
    public async Task Only_api_releases_of_current_apis_get_extracted()
    {
        var gen = from fixture in Fixture.Generate()
                  from apimDtos in Generator.ResourceDtos
                      // Ensure we have at least one API release whose parent API is not the current revision
                  where apimDtos.Keys.Any(key => key.Resource is ApiReleaseResource or WorkspaceApiReleaseResource
                                                 && key.Parents.Any(parent => (parent.Resource is ApiResource or WorkspaceApiResource)
                                                                               && ApiRevisionModule.IsRootName(parent.Name) is false))
                  let writtenResourceKeys = new List<ResourceKey>()
                  select (writtenResourceKeys, fixture with
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
                      ShouldExtract = async (key, _, cancellationToken) =>
                      {
                          await ValueTask.CompletedTask;
                          return true;
                      },
                      WriteResource = async (resourceKey, dtoOption, cancellationToken) =>
                      {
                          await ValueTask.CompletedTask;
                          writtenResourceKeys.Add(resourceKey);
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (writtenResourceKeys, fixture) = tuple;
            var runExtractor = fixture.Resolve();

            // Act
            await runExtractor(CancellationToken);

            // Assert that the parent APIs of all written releases are the current revision
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
                ShouldExtract = (_, _, _) => ValueTask.FromResult(true),
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
                  from dtoOption in Gen.Const(new JsonObject()).OptionOf()
                  select (resourceKey, dtoOption, fixture with
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
            var (resourceKey, dtoOption, fixture) = tuple;
            var shouldExtract = fixture.Resolve();

            // Act
            var extract = await shouldExtract(resourceKey, dtoOption, CancellationToken);

            // Assert that the resource should be extracted
            await Assert.That(extract)
                        .IsTrue();
        });
    }

    [Test]
    public async Task Resources_not_in_configuration_are_not_extracted()
    {
        var gen = from fixture in Fixture.Generate()
                  from resourceKey in Generator.ResourceKey
                  from dtoOption in Gen.Const(new JsonObject()).OptionOf()
                  select (resourceKey, dtoOption, fixture with
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
            var (resourceKey, dtoOption, fixture) = tuple;
            var shouldExtract = fixture.Resolve();

            // Act
            var extract = await shouldExtract(resourceKey, dtoOption, CancellationToken);

            // Assert that the resource should not be extracted
            await Assert.That(extract).IsFalse();
        });
    }

    [Test]
    public async Task Unknown_resources_are_extracted()
    {
        var gen = from fixture in Fixture.Generate()
                  from resourceKey in Generator.ResourceKey
                  from dtoOption in Gen.Const(new JsonObject()).OptionOf()
                  select (resourceKey, dtoOption, fixture with
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
            var (resourceKey, dtoOption, fixture) = tuple;
            var shouldExtract = fixture.Resolve();

            // Act
            var extract = await shouldExtract(resourceKey, dtoOption, CancellationToken);

            // Assert that the resource should be extracted
            await Assert.That(extract).IsTrue();
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
                  from dtoOption in Gen.Const(new JsonObject()).OptionOf()
                  select (resourceKey, dtoOption, fixture with
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
            var (resourceKey, dtoOption, fixture) = tuple;
            var shouldExtract = fixture.Resolve();

            // Act
            var extract = await shouldExtract(resourceKey, dtoOption, CancellationToken);

            // Assert that the resource should not be extracted
            await Assert.That(extract).IsFalse();
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
                  from fixture in Fixture.Generate()
                  let writtenDtos = new List<JsonObject>()
                  let dto = new JsonObject
                  {
                      ["id"] = resourceKey.ToString()
                  }
                  select (resourceKey, dto, writtenDtos, fixture with
                  {
                      WriteInformationFile = async (resource, name, dto, parents, cancellationToken) =>
                      {
                          await ValueTask.CompletedTask;
                          writtenDtos.Add(dto);
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (resourceKey, dto, writtenDtos, fixture) = tuple;
            var writeResource = fixture.Resolve();

            // Act
            await writeResource(resourceKey, dto, CancellationToken);

            // Assert that the DTO was written
            var writtenDto = await Assert.That(writtenDtos)
                                         .HasSingleItem();

            await Assert.That(writtenDto)
                        .IsEqualTo(dto);
        });
    }

    [Test]
    public async Task Information_file_without_dto_is_not_written()
    {
        var gen = from resourceKey in Generator.GenerateResourceKey(resource => resource is IResourceWithInformationFile)
                  let dtoOption = Option<JsonObject>.None()
                  from fixture in Fixture.Generate()
                  let writtenDtos = new List<JsonObject>()
                  select (resourceKey, dtoOption, writtenDtos, fixture with
                  {
                      WriteInformationFile = async (resource, name, dto, parents, cancellationToken) =>
                      {
                          await ValueTask.CompletedTask;
                          writtenDtos.Add(dto);
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (resourceKey, dtoOption, writtenDtos, fixture) = tuple;
            var writeResource = fixture.Resolve();

            // Act
            await writeResource(resourceKey, dtoOption, CancellationToken);

            // Assert that nothing was written
            await Assert.That(writtenDtos)
                        .IsEmpty();
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
                  let writtenDtos = new List<JsonObject>()
                  from fixture in Fixture.Generate()
                  select (resourceKey, dto, writtenDtos, fixture with
                  {
                      WritePolicyFile = async (resource, name, dto, parents, cancellationToken) =>
                      {
                          await ValueTask.CompletedTask;
                          writtenDtos.Add(dto);
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (resourceKey, dto, writtenDtos, fixture) = tuple;
            var writeResource = fixture.Resolve();

            // Act
            await writeResource(resourceKey, dto, CancellationToken);

            // Assert that the DTO was written
            var writtenDto = await Assert.That(writtenDtos)
                                         .HasSingleItem();

            await Assert.That(writtenDto)
                        .IsEqualTo(dto);
        });
    }

    [Test]
    public async Task Policy_file_without_dto_is_not_written()
    {
        var gen = from resourceKey in Generator.GenerateResourceKey(resource => resource is IPolicyResource)
                  let dtoOption = Option<JsonObject>.None()
                  let writtenDtos = new List<JsonObject>()
                  from fixture in Fixture.Generate()
                  select (resourceKey, dtoOption, writtenDtos, fixture with
                  {
                      WritePolicyFile = async (resource, name, dto, parents, cancellationToken) =>
                      {
                          await ValueTask.CompletedTask;
                          writtenDtos.Add(dto);
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (resourceKey, dtoOption, writtenDtos, fixture) = tuple;
            var writeResource = fixture.Resolve();

            // Act
            await writeResource(resourceKey, dtoOption, CancellationToken);

            // Assert that nothing was written
            await Assert.That(writtenDtos)
                        .IsEmpty();
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
                  let writtenSpecifications = new List<string>()
                  from fixture in Fixture.Generate()
                  select (resourceKey, dto, specificationContents, writtenSpecifications, fixture with
                  {
                      GetApiSpecificationFromApim = async (resourceKey, dto, cancellationToken) =>
                      {
                          await ValueTask.CompletedTask;
                          var specification = ApiSpecification.OpenApi.GraphQl.Instance as ApiSpecification;
                          return Option.Some((specification, specificationContents));
                      },
                      WriteApiSpecificationFile = async (resourceKey, specification, contents, cancellationToken) =>
                      {
                          await ValueTask.CompletedTask;
                          writtenSpecifications.Add(contents.ToString());
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (resourceKey, dto, specificationContents, writtenSpecifications, fixture) = tuple;
            var writeResource = fixture.Resolve();

            // Act
            await writeResource(resourceKey, dto, CancellationToken);

            // Assert that the specification was written
            var writtenSpecification = await Assert.That(writtenSpecifications)
                                                   .HasSingleItem();

            await Assert.That(writtenSpecification)
                        .IsEqualTo(specificationContents.ToString());
        });
    }

    [Test]
    public async Task Missing_api_specification_is_not_written()
    {
        var gen = from resourceKey in Generator.GenerateResourceKey(resource => resource is ApiResource or WorkspaceApiResource)
                  let dtoOption = Option.Some(new JsonObject())
                  let writtenSpecifications = new List<string>()
                  from fixture in Fixture.Generate()
                  select (resourceKey, dtoOption, writtenSpecifications, fixture with
                  {
                      GetApiSpecificationFromApim = async (resourceKey, dto, cancellationToken) =>
                      {
                          await ValueTask.CompletedTask;
                          return Option.None;
                      },
                      WriteApiSpecificationFile = async (resourceKey, specification, contents, cancellationToken) =>
                      {
                          await ValueTask.CompletedTask;
                          writtenSpecifications.Add(contents.ToString());
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (resourceKey, dtoOption, writtenSpecifications, fixture) = tuple;
            var writeResource = fixture.Resolve();

            // Act
            await writeResource(resourceKey, dtoOption, CancellationToken);

            // Assert that nothing was written
            await Assert.That(writtenSpecifications)
                        .IsEmpty();
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