# Integration tests architecture

## Overview

We use property-based integration tests to validate the extractor/publisher against a live APIM instance. [CsCheck](https://github.com/AnthonyLloyd/CsCheck) generates random properties that can be reproduced deterministically via seeds.

## Prerequisites

- The following configuration values are set (environment variables, appsettings.json, command-line arguments, etc):
    - `API_MANAGEMENT_SERVICE_NAME` or `apimServiceName`
    - `AZURE_RESOURCE_GROUP_NAME`
    - `AZURE_SUBSCRIPTION_ID`
- The executable can authenticate to Azure (managed identity, `az login` has already been run, `AZURE_BEARER_TOKEN` passed to configuration, etc).

## Test pipeline

Each test iteration runs the following steps against a randomly generated `TestState`:

1. **Filtered extraction**: populate APIM, extract with a random filter, validate only filtered resources appear on disk.
2. **Unfiltered extraction**: extract everything, validate all resources appear on disk.
3. **Publish without overrides**: wipe APIM, publish extracted artifacts, validate APIM matches `TestState`.
4. **Publish with overrides**: publish again with a random `PublisherOverride`, validate APIM reflects overridden values.
5. **Commit-based publish**: wipe APIM, re-populate with `TestState`, set up a git commit transitioning from `TestState` to `NextState`, publish using a commit ID, validate APIM reflects the state transition correctly. Note that the Git commit setup uses three steps:
    - Commit 1: `initialState`
    - Commit 2: `nextState`. This is the commit ID that will passed to the publisher.
    - Commit 3: `initialState` again. This ensures that the respects the specified commit ID. Previous buggy iterations of the publisher used the latest commit ID instead of the specified one.

## Key types

### `ITestModel` / `ITestModel<T>`

Every resource type has a model record implementing `ITestModel<T>`. The interface requires:

| Member | Purpose |
|--------|---------|
| `Key` | `ResourceKey` identifying the resource (type + name + parents) |
| `ToDto()` | Produces the JSON DTO used in the APIM PUT |
| `ValidateDto(dto)` | Validates that the DTO (from an APIM GET or extracted file) matches expectations |
| `static GenerateSet(models)` | Generates a random set of initial models. Receives all models generated so far (by predecessor resource types), enabling cross-resource dependencies. |
| `static GenerateUpdates(models)` | Generates updated versions of existing models (for overrides) |
| `static GenerateNextState(previousModels, accumulatedNextModels)` | Generates a new state for commit-based publish. `previousModels` is the full previous `TestState`; `accumulatedNextModels` is the partial next state built so far by predecessor types. |

### `TestState`

A plain record holding all resource models for a test run.

| Member | Purpose |
|--------|--------|
| `Models` | All resource test models |

### `TestStateModule`

| Delegate | Purpose |
|----------|--------|
| `GenerateTestState` | Generates a random initial `TestState` by folding over resource types in topological order. Calls each type's `GenerateSet`. |
| `GenerateNextTestState` | Generates a subsequent `TestState` by folding over resource types in topological order. Calls each type's `GenerateNextState`. |

### `TestsModule`

Central registration and test orchestration.

| Member | Purpose |
|--------|---------|
| `ResourceModels` (`ImmutableDictionary<IResource, Type>`) |  Single registration point that maps resource instances to model types |
| `Resources` (`ImmutableHashSet<IResource>`) | List of all resources in scope for testing (`ResourceModels.Keys`) |
| `ConfigureRunTests` | Registers all test delegates into the DI container |
| `ResolveRunTests` | Runs the test pipeline |

### `PublisherOverride`

| Member | Purpose |
|--------|---------|
| `Updates` (`ImmutableDictionary<ResourceKey, ITestModel>`) |  Maps resources to their overridden models |
| `Serialize()` | Produces a `JsonObject` that the publisher can read for overrides |
| `static Generate(models)` | Loops over registered types generates overrides for a random subset of models |

### `ExtractorFilter`

Controls which resources the extractor should extract. Serialized to YAML.

| Member | Purpose |
|--------|---------|
| `Resources` | Maps parents to child resources that should be extracted |
| `ShouldExtract(key)` | Returns whether a `ResourceKey` passes the filter. If a parent is not in `Resources`, all its children are extracted. |
| `Serialize()` | Produces a `JsonObject` that the extractor can read for filters |
| `static Generate(models)` | Generates a random filter given a list of models |

### `CommonModule`


| Member | Notes |
|--------|--------|
| `SortResources` delegate | Takes a list of resources and sorts them in topological order |
| `GenerateDisplayName(name)` | Output: `"myname-display"` |
| `GenerateDisplayName(name, current)` | Output: `"myname-display-2"` -> `"myname-display-3"` |
| `GenerateDescription(name)` | Output: `"myname-description"` |
| `GenerateDescription(name, current)` | Output:  `"myname-description-2"` -> `"myname-description-3"` |

## Resource Models

| Resource | Model | Properties | Generation | Validation | Notes |
|----------|-------|------------|------------|------------|-------|
| Tag | `TagModel` | `DisplayName` | Generate 0–5 tags. <br><br> Deduplicate by `Key` and by `DisplayName`. | Always validate `DisplayName`. | |
| Named Value | `NamedValueModel` | `DisplayName` <br> `Value` <br> `Secret` | Generate 0–5 named values. <br><br> `Secret` is randomly `true` or `false`. <br><br> Deduplicate by `Key` and by `DisplayName`. | Always validate `DisplayName` and `Secret`. <br><br> Only validate `Value` when `Secret == false` (APIM doesn't return secret values). | Publisher validation skips secret named values unless overridden. |
| Logger | `LoggerModel` | `Description` <br> `IsBuffered` | Only use `azuremonitor` logger. APIM requires real resources for other application insights and event hub. <br><br> Generate 0 or 1 logger. APIM supports a maximum of 1 logger of type `azuremonitor`. Put extra weight on the 1 logger scenario so it's more likely to be generated than 0. | Always validate `Description` and `IsBuffered`. | Diagnostics depend on loggers via `LoggerId`. |
| Diagnostic | `DiagnosticModel` | `Verbosity` <br> `LogClientIp` <br> `LoggerKey` | For each logger in the accumulated models, generate 0 or 1 diagnostic. Diagnostic name matches the logger name (APIM requires service-level diagnostic names to match logger types). <br><br> Put extra weight on the 1 diagnostic scenario. <br><br> `LoggerId` in the DTO is a relative ID (e.g. `/loggers/azuremonitor`). | Validate `Verbosity` and `LogClientIp`. <br><br> Skip `LoggerId` validation (format differs between APIM GET and extracted files). | First resource with a cross-resource dependency. Depends on `LoggerResource` via `IResourceWithReference`. <br><br> `GenerateNextState` delegates to `GenerateSet(accumulatedNextModels)` — diagnostics are fully regenerated based on loggers in the new state. |
| Product | `ProductModel` | `DisplayName` <br> `Description` | Generate 0–5 products. <br><br> Deduplicate by `Key` and by `DisplayName`. | Always validate `DisplayName` and `Description`. | APIM auto-creates subscriptions and product-groups when a product is created. `PutProductInApim` in `common` deletes these auto-created resources to avoid polluting state. |
| Group | `GroupModel` | `DisplayName` <br> `Description` | Generate 0–5 groups. <br><br> Deduplicate by `Key` and by `DisplayName`. | Always validate `DisplayName` and `Description`. | Built-in groups (Administrators, Developers, Guests) are excluded by `IsResourceKeySupported`. |
| Version Set | `VersionSetModel` | `DisplayName` <br> `Description` | Generate 0–5 version sets. <br><br> Deduplicate by `Key` and by `DisplayName`. | Always validate `DisplayName` and `Description`. | APIM requires `versioningScheme` on PUT. We use a fixed value (`"Segment"`) in the DTO. |

## DI pattern

Each module follows the `Configure*` / `Resolve*` pattern:
- `Configure*(IHostApplicationBuilder)` registers dependencies
- `Resolve*(IServiceProvider)` returns `*` by using configured dependencies

Some operations in the `common` project require a singleton `ServiceDirectory` in DI. However, our tests generate a random `ServiceDirectory` during each test iteration, and after DI is resolved. To work around this, we:
- Create an empty application builder
- Pass the `ServiceDirectory` to the builder's configuration
- Pass the builder to the `Configure*` method in `common` that requires a service directory
- Build the builder's service provider.
- Return `*` from the built service provider.

## Useful delegates

### `IsResourceKeySupported`

Filters out resources that tests should not touch:
- Groups: Administrators, Developers, Guests
- Subscriptions: Master
- Any resource type not in `TestsModule.Resources`
- Any resource type that the APIM SKU doesn't support (e.g. Gateways in the Consumption SKU)

### `WriteGitCommit`

Writes APIM artifacts in `TestState` to disk, creates a git commit, and returns a commit ID.

## Style notes

- Use `ResourceKey.From(...)` for simple construction, and `new ResourceKey { ... }` when parameters are complex
- Use `Gen.Frequency` for weighted generation when uniform distribution isn't appropriate
- Use local functions for helpers inside resolvers. Make them static if they're pure. Make them class methods if they're shared.

## Adding a new resource type

1. Create `MyResource.cs` with a record implementing `ITestModel<MyResourceModel>`
2. Implement `GenerateSet`, `GenerateUpdates`, `GenerateNextState`, `ToDto`, `ValidateDto`. The compiler should let you know if you're missing methods.
3. Register in `TestsModule.ResourceModels`: `[MyResource.Instance] = typeof(MyResourceModel)`.
4. Add any required special handling to the extractor and publisher validation.
5. That's it. The type will be picked up automatically via reflection where needed.