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
4. **Publish with overrides**: publish again with a random `PublisherOverride`, validate APIM reflects overridden values. `PublisherOverride` is created from a generated updated subset of `TestState`.
5. **Commit-based publish**: wipe APIM, re-populate with `TestState`, set up a git commit transitioning from `TestState` to `NextState`, publish using a commit ID, validate APIM reflects the state transition correctly. Note that the Git commit setup uses three steps:
    - Commit 1: `initialState`
    - Commit 2: `nextState`. This is the commit ID that will passed to the publisher.
    - Commit 3: `initialState` again. This ensures that the publisher respects the specified commit ID. Previous buggy iterations of the publisher used the latest commit ID instead of the specified one.


## Resource models

| Model | Generation | Notes |
|-------|------------|-------|
| `TagModel` | Generate 0–5 tags. <br><br> Deduplicate by `Key` and by `DisplayName`. | |
| `NamedValueModel` | Generate 0–5 named values. <br><br> Deduplicate by `Key` and by `DisplayName`. | `Value` is only validated when `Secret == false` (APIM doesn't return secret values). <br><br> Publisher validation skips secret named values unless overridden. |
| `LoggerModel` | Generate 0–1 (singleton) with weighted frequency favoring one logger. <br><br> Only use `azuremonitor`. | |
| `DiagnosticModel` | Select a random subset of logger models and generate one diagnostic per selected logger. <br><br> Diagnostic name matches the logger name. <br><br> `LoggerId` in the DTO is a relative ID (e.g. `/loggers/azuremonitor`). <br><br> For next state, regenerate all diagnostics based on available loggers. | Skip `LoggerId` validation (format differs between APIM GET and extracted files). |
| `ProductModel` | Generate 0–5 products. <br><br> Deduplicate by `Key` and by `DisplayName`. | APIM auto-creates subscriptions and product-groups when a product is created. `PutProductInApim` in `common` deletes these auto-created resources to avoid polluting state. |
| `ApiModel` | Each generated API is a "group": a root API plus 0–3 additional revisions sharing `DisplayName`, `Type`, `Path`, etc. <br><br> Generate API type from OpenAPI, WADL, WSDL, and GraphQL (WebSocket currently excluded). <br><br> `ServiceUrl` is required for WADL/WSDL; optional for others. <br><br> Optionally attach APIs to version sets. <br><br> Deduplicate by `DisplayName` and by `VersionSetName` (no two API groups in the same version set). <br><br> For next state, keep/update/add API groups. | `ServiceUrl`, `OperationNames`, and `Specification` are validated during extractor validation, not via `ValidateDto`. <br><br> Non-current revisions can't update certain properties (`DisplayName`, `Type`, `Description`, `Path`, etc.) — the publisher copies these from the current revision during PUT. <br><br> WSDL import destroys the `Description` field; publisher saves and restores it after import (root API only). <br><br> Extractor expects no specification file for WSDL. APIM randomly generates WADL/WSDL operation names, so extractor skips operation validation for those types. |
| `ApiPolicyModel` | Select a random subset of APIs and generate one policy per selected API. <br><br> Policy name is always `policy`. <br><br> `Content` is a random valid policy XML. It occasionally references named values and policy fragments. | |
| `ProductPolicyModel` | Select a random subset of products and generate one policy per selected product. <br><br> Policy name is always `policy`. <br><br> `Content` is a random valid policy XML. It occasionally references named values and policy fragments. | |
| `ApiOperationPolicyModel` | Generate one policy per selected API operation, from a random subset of API operations. <br><br> Policy name is always `policy`. <br><br> Skip WSDL and WADL APIs because APIM may generate operation names. | |
| `GroupModel` | Generate 0–5 groups. <br><br> Deduplicate by `Key` and by `DisplayName`. | Built-in groups (Administrators, Developers, Guests) are excluded by `IsResourceKeySupported`. |
| `ProductGroupModel` | Pair all products with all groups and pick a random subset. <br><br> Name is always `{groupName}`. | Validates DTO structure (`name` equals `Key.Name`, `properties.groupId` ends with `"groups/GroupName"`). <br><br> Publisher override set always empty. Links have nothing to override. |
| `VersionSetModel` | Generate 0–5 version sets. <br><br> Deduplicate by `Key` and by `DisplayName`. | APIM requires `versioningScheme` on PUT. We use a fixed value (`"Segment"`) in the DTO. |
| `BackendModel` | Generate 0–5 backends. <br><br> Deduplicate by `Key`. | APIM requires `protocol` on PUT. We use a fixed value (`"http"`) in the DTO. |
| `GatewayModel` | Disabled. | Disabled on Developer SKU due to [classic tier limits](https://learn.microsoft.com/en-us/azure/azure-resource-manager/management/azure-subscription-service-limits#limits---api-management-classic-tiers). Revisit once clarified. <br><br> APIM requires `locationData` on PUT. We use a fixed value (`"test"`) in the DTO. |
| `PolicyFragmentModel` | Generate 0–5 policy fragments. <br><br> `Content` is a random valid policy fragment XML. It occasionally references named values. | |
| `ServicePolicyModel` | Generate 0–1 (singleton) with weighted frequency favoring one policy. <br><br> Policy name is always `policy`. <br><br> `Content` is a random valid policy XML. It occasionally references named values and policy fragments. | |

## Style notes

- Use `ResourceKey.From(...)` for simple construction, and `new ResourceKey { ... }` when parameters are complex
- Use `Gen.Frequency` for weighted generation when uniform distribution isn't appropriate
- Use local functions for helpers inside resolvers. Make them static if they're pure. Make them class methods if they're shared.
- Each module follows the `Configure*` / `Resolve*` pattern:
  - `Configure*(IHostApplicationBuilder)` registers dependencies
  - `Resolve*(IServiceProvider)` returns `*` by using configured dependencies
- Some operations in the `common` project require a singleton `ServiceDirectory` in DI. However, our tests generate a random `ServiceDirectory` during each test iteration, and after DI is resolved. To work around this, we:
  - Create an empty application builder
  - Pass the `ServiceDirectory` to the builder's configuration
  - Pass the builder to the `Configure*` method in `common` that requires a service directory
  - Build the builder's service provider.
  - Return `*` from the built service provider.

## Adding a new resource type

1. Create `MyResource.cs` with a record implementing `ITestModel<MyResourceModel>`
2. Implement `GenerateSet`, `GenerateUpdates`, `GenerateNextState`, `ToDto`, `ValidateDto`. The compiler should let you know if you're missing methods.
3. Register in `TestsModule.ResourceModels`: `[MyResource.Instance] = typeof(MyResourceModel)`.
4. Add any required special handling to the extractor and publisher validation.
5. That's it. The type will be picked up automatically via reflection where needed.