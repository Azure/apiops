using common;
using common.tests;
using CsCheck;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace extractor.tests;

internal sealed class RunExtractorTests
{
    [Test]
    public async Task Unsupported_resources_are_not_extracted()
    {
        var gen = Fixture.Generate();

        await gen.SampleAsync(async fixture =>
        {
            // Arrange
            var writtenResources = ImmutableArray<IResource>.Empty;
            fixture = fixture with
            {
                IsResourceSupportedInApim = async (resource, cancellationToken) =>
                {
                    await ValueTask.CompletedTask;
                    return resource.GetHashCode() % 2 == 0;
                },
                WriteResource = async (resourceKey, dtoOption, cancellationToken) =>
                {
                    await ValueTask.CompletedTask;
                    ImmutableInterlocked.Update(ref writtenResources, array => array.Add(resourceKey.Resource));
                }
            };
            var runExtractor = fixture.Resolve();
            var cancellationToken = TestContext.Current!.Execution.CancellationToken;

            // Act
            await runExtractor(cancellationToken);

            // Assert
            var unsupportedWrittenResources =
                await writtenResources.ToAsyncEnumerable()
                                      .Where(async (resource, cancellationToken) => await fixture.IsResourceSupportedInApim(resource, cancellationToken) is false)
                                      .ToArrayAsync(cancellationToken);

            await Assert.That(unsupportedWrittenResources).IsEmpty();
        });
    }

    [Test]
    public async Task Descendants_of_unsupported_resources_are_not_extracted()
    {
        var gen = Fixture.Generate();

        await gen.SampleAsync(async fixture =>
        {
            // Arrange
            var writtenResources = ImmutableArray<IResource>.Empty;
            fixture = fixture with
            {
                IsResourceSupportedInApim = async (resource, cancellationToken) =>
                {
                    await ValueTask.CompletedTask;
                    return resource.GetHashCode() % 2 == 0;
                },
                WriteResource = async (resourceKey, dtoOption, cancellationToken) =>
                {
                    await ValueTask.CompletedTask;
                    ImmutableInterlocked.Update(ref writtenResources, array => array.Add(resourceKey.Resource));
                }
            };
            var runExtractor = fixture.Resolve();
            var cancellationToken = TestContext.Current!.Execution.CancellationToken;

            // Act
            await runExtractor(cancellationToken);

            // Assert
            var unsupportedWrittenAncestors =
                await writtenResources.SelectMany(resource => resource.GetTraversalPredecessorHierarchy())
                                      .ToAsyncEnumerable()
                                      .Where(async (resource, cancellationToken) => await fixture.IsResourceSupportedInApim(resource, cancellationToken) is false)
                                      .ToArrayAsync(cancellationToken);

            await Assert.That(unsupportedWrittenAncestors).IsEmpty();
        });
    }

    [Test]
    public async Task Filtered_out_resources_are_not_extracted()
    {
        var gen = Fixture.Generate();

        await gen.SampleAsync(async fixture =>
        {
            // Arrange
            var writtenResourceKeys = ImmutableArray<ResourceKey>.Empty;
            fixture = fixture with
            {
                ShouldExtract = async (resourceKey, cancellationToken) =>
                {
                    await ValueTask.CompletedTask;
                    return resourceKey.GetHashCode() % 2 == 0;
                },
                WriteResource = async (resourceKey, dtoOption, cancellationToken) =>
                {
                    await ValueTask.CompletedTask;
                    ImmutableInterlocked.Update(ref writtenResourceKeys, array => array.Add(resourceKey));
                }
            };
            var runExtractor = fixture.Resolve();
            var cancellationToken = TestContext.Current!.Execution.CancellationToken;

            // Act
            await runExtractor(cancellationToken);

            // Assert
            var filteredOutWrittenResourceKeys =
                await writtenResourceKeys.ToAsyncEnumerable()
                                         .Where(async (resourceKey, cancellationToken) => await fixture.ShouldExtract(resourceKey, cancellationToken) is false)
                                         .ToArrayAsync(cancellationToken);

            await Assert.That(filteredOutWrittenResourceKeys).IsEmpty();
        });
    }

    [Test]
    public async Task Written_dtos_match_apim_dtos()
    {
        var gen = Fixture.Generate();

        await gen.SampleAsync(async fixture =>
        {
            // Arrange
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
            var cancellationToken = TestContext.Current!.Execution.CancellationToken;

            // Act
            await runExtractor(cancellationToken);

            // Assert
            await writtenDtos.IterTaskParallel(async kvp =>
            {
                var (resource, name, parents, dto) = (kvp.Key.Resource, kvp.Key.Name, kvp.Key.Parents, kvp.Value);
                var resourceWithDto = (IResourceWithDto)resource;

                var apimDtos = await fixture.ListResourceDtosFromApim(resourceWithDto, parents, cancellationToken)
                                            .Where(tuple => tuple.Name == name)
                                            .Select(tuple => tuple.Dto)
                                            .ToArrayAsync(cancellationToken);

                var apimDto = await Assert.That(apimDtos).HasSingleItem();
                await Assert.That(apimDto).IsEqualTo(dto);
            }, maxDegreeOfParallelism: Option.None, cancellationToken);
        });
    }

    [Test]
    public async Task Only_api_releases_of_current_apis_get_extracted()
    {
        var gen = Fixture.Generate();

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
            var cancellationToken = TestContext.Current!.Execution.CancellationToken;

            // Act
            await runExtractor(cancellationToken);

            // Assert
            var writtenReleasesWithoutCurrentApis =
                writtenResourceKeys
                    // Get API releases...
                    .Where(key => key.Resource is ApiReleaseResource or WorkspaceApiReleaseResource)
                    // ...whose parent API is not the current revision
                    .Where(key => key.Parents
                                     .Any(x => x.Resource is ApiResource or WorkspaceApiResource
                                                && ApiRevisionModule.IsRootName(x.Name) is false));

            await Assert.That(writtenReleasesWithoutCurrentApis).IsEmpty();
        });
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
            select new Fixture
            {
                Graph = ResourceGraph.From(resourceDtos.Keys.Select(key => key.Resource), CancellationToken.None),
                IsResourceSupportedInApim = (_, _) => ValueTask.FromResult(true),
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
    [Test]
    public async Task Resources_in_configuration_are_extracted()
    {
        var gen = from fixture in Fixture.Generate()
                  from resourceKey in Generator.ResourceKey
                  select (resourceKey, fixture);

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (resourceKey, fixture) = tuple;
            fixture = fixture with
            {
                ResourceIsInConfiguration = (_, _) => ValueTask.FromResult(Option.Some(true))
            };
            var shouldExtract = fixture.Resolve();
            var cancellationToken = TestContext.Current!.Execution.CancellationToken;

            // Act
            var result = await shouldExtract(resourceKey, cancellationToken);

            // Assert
            await Assert.That(result).IsTrue();
        });
    }

    [Test]
    public async Task Resources_not_in_configuration_are_not_extracted()
    {
        var gen = from fixture in Fixture.Generate()
                  from resourceKey in Generator.ResourceKey
                  select (resourceKey, fixture);

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (resourceKey, fixture) = tuple;
            fixture = fixture with
            {
                ResourceIsInConfiguration = (_, _) => ValueTask.FromResult(Option.Some(false))
            };
            var shouldExtract = fixture.Resolve();
            var cancellationToken = TestContext.Current!.Execution.CancellationToken;

            // Act
            var result = await shouldExtract(resourceKey, cancellationToken);

            // Assert
            await Assert.That(result).IsFalse();
        });
    }

    [Test]
    public async Task Unknown_resources_are_extracted()
    {
        var gen = from fixture in Fixture.Generate()
                  from resourceKey in Generator.ResourceKey
                  select (resourceKey, fixture);

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (resourceKey, fixture) = tuple;
            fixture = fixture with
            {
                ResourceIsInConfiguration = (_, _) => ValueTask.FromResult(Option<bool>.None())
            };
            var shouldExtract = fixture.Resolve();
            var cancellationToken = TestContext.Current!.Execution.CancellationToken;

            // Act
            var result = await shouldExtract(resourceKey, cancellationToken);

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
                  select (resourceKey, fixture);

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (resourceKey, fixture) = tuple;
            fixture = fixture with
            {
                ResourceIsInConfiguration = (_, _) => ValueTask.FromResult(Option.Some(true))
            };
            var shouldExtract = fixture.Resolve();
            var cancellationToken = TestContext.Current!.Execution.CancellationToken;

            // Act
            var result = await shouldExtract(resourceKey, cancellationToken);

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
            var cancellationToken = TestContext.Current!.Execution.CancellationToken;

            // Act
            await writeResource(resourceKey, dto, cancellationToken);

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
            var cancellationToken = TestContext.Current!.Execution.CancellationToken;

            // Act
            await writeResource(resourceKey, dtoOption, cancellationToken);

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
            var cancellationToken = TestContext.Current!.Execution.CancellationToken;

            // Act
            await writeResource(resourceKey, dto, cancellationToken);

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
            var cancellationToken = TestContext.Current!.Execution.CancellationToken;

            // Act
            await writeResource(resourceKey, dtoOption, cancellationToken);

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
            var writtenSpecification = BinaryData.FromObjectAsJson(new JsonObject());
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
                    writtenSpecification = contents;
                }
            };
            var writeResource = fixture.Resolve();
            var cancellationToken = TestContext.Current!.Execution.CancellationToken;

            // Act
            await writeResource(resourceKey, dto, cancellationToken);

            // Assert
            await Assert.That(writtenSpecification.ToString()).IsEquivalentTo(specificationContents.ToString());
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
            var cancellationToken = TestContext.Current!.Execution.CancellationToken;

            // Act
            await writeResource(resourceKey, dtoOption, cancellationToken);

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
                GetApiSpecificationFromApim = (_, _, _) => ValueTask.FromResult(Option<(ApiSpecification, BinaryData)>.None()),
                WriteApiSpecificationFile = (_, _, _, _) => ValueTask.CompletedTask
            });
    }
}