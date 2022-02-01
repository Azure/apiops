---
title: Tools
parent: Configure APIM tools
has_children: false
nav_order: 0
---

This section describes the extractor and creator components which form the core tools used to deploy and publish changes using APIOps.

## Extractor
The extractor generates APIOps artifacts from an existing APIM instance. These artifacts can then be used as the source of truth for your APIM environment; make changes to them and have a CI/CD process update your Azure environment (with the creator tool, for instance).

### Parameters
The tool expects certain configuration parameters. These can be passed as environment variables, command line arguments, etc. It will look for variables using the [``Host.CreateDefaultBuilder(arguments)``](https://docs.microsoft.com/en-us/dotnet/api/microsoft.extensions.hosting.host.createdefaultbuilder?view=dotnet-plat-ext-6.0#Microsoft_Extensions_Hosting_Host_CreateDefaultBuilder_System_String___) settings. Here are the expected parameters:

| Variable | Purpose |
| - | - |
| AZURE_SUBSCRIPTION_ID | Subscription ID of the APIM instance to be extracted |
| AZURE_RESOURCE_GROUP_NAME | Resource group name of the APIM instance to be extracted |
| AZURE_BEARER_TOKEN | Token for authentication to Azure. If this is not specified, the tool authenticate with  the [``DefaultAzureCredential``](https://docs.microsoft.com/en-us/dotnet/api/azure.identity.defaultazurecredential?view=azure-dotnet). |
| API_MANAGEMENT_SERVICE_NAME | Name of the APIM instance to be extracted |
| API_MANAGEMENT_SERVICE_OUTPUT_FOLDER_PATH | Folder where the APIM artifacts will be saved |
| API_SPECIFICATION_FORMAT | OpenAPI specification format. Valid options are **JSON** or **YAML**. If the variable is missing or invalid, **YAML** will be used by default. |

### Artifacts
The extractor will export the artifacts listed below.
> Note that all artifacts gets exported in parallel. We use retry up to 10 times with exponential backoff in case Azure throttles the requests, but keep this in mind in case you have many APIs/operations and experience IO issues.

| Type | Path |
| - | - |
| APIM instance information | ./serviceInformation.json |
| APIM instance global policy | ./policy.xml |
| Product information | ./products/``product_display_name``/productInformation.json |
| Product policy | ./products/``product_display_name``/policy.xml |
| Gateway information | ./gateways/``gateway_name``/gatewayInformation.json |
| Authorization server information | ./authorizationServers/``authorization_server_name``/authorizationServerInformation.json |
| Diagnostic information | ./diagnostics/``diagnostic_name``/diagnosticInformation.json |
| Logger information | ./loggers/``logger_name``/loggerInformation.json |
| API information | ./apis/``api_display_name``/apiInformation.json |
| OpenAPI specification | ./apis/``api_display_name``/specification.{yaml\|json} |
| API policy | ./apis/``api_display_name``/policy.xml |
| Operation policy | ./apis/``api_display_name``/operations/``operation_display_name``/policy.xml |

## Creator
The creator tool updates the Azure APIM instance with the artifact folder contents. If a commit ID is specified in the parameters, it will update the instance with only files that were changed by the commit.
### Parameters
The tool expects certain configuration parameters. These can be passed as environment variables, command line arguments, etc. It will look for variables using the [``Host.CreateDefaultBuilder(arguments)``](https://docs.microsoft.com/en-us/dotnet/api/microsoft.extensions.hosting.host.createdefaultbuilder?view=dotnet-plat-ext-6.0#Microsoft_Extensions_Hosting_Host_CreateDefaultBuilder_System_String___) settings. Here are the expected parameters:

| Variable | Purpose |
| - | - |
| AZURE_SUBSCRIPTION_ID | Subscription ID of the APIM instance to be updated |
| AZURE_RESOURCE_GROUP_NAME | Resource group name of the APIM instance to be updated |
| API_MANAGEMENT_SERVICE_OUTPUT_FOLDER_PATH | Folder where the APIM artifacts are located |
| AZURE_BEARER_TOKEN | Token for authentication to Azure. If this is not specified, the tool authenticate with  the [``DefaultAzureCredential``](https://docs.microsoft.com/en-us/dotnet/api/azure.identity.defaultazurecredential?view=azure-dotnet). |
| COMMIT_ID | Git commit ID. If specified, the tool will only use files that were affected by that commit. New/modified files will be updated in Azure, and deleted artifacts will be removed from the Azure APIM instance. If unspecified, the tool will do a Put operation on the Azure APIM instance with all files in the artifacts folder. |