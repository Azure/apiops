---
title: Tools - Publisher
parent: Configure APIM tools
has_children: false
nav_order: 2
---

This section describes the Publisher component which forms the core tool used to publish the updates to the Azure APIM instance.

## Publisher
The Publisher tool updates the Azure APIM instance with the artifact folder contents. If a commit ID is specified in the parameters, it will update the instance with only files that were changed by the commit. In addition, the publisher tool picks up changes in the configuration yaml file when running the publisher. The configuration file is the only file outside of the artifacts folder that gets picked up by the publisher tool when promoting changes across environments.
### Parameters
The tool expects certain configuration parameters. These can be passed as environment variables, command line arguments, etc. It will look for variables using the [``Host.CreateDefaultBuilder(arguments)``](https://docs.microsoft.com/en-us/dotnet/api/microsoft.extensions.hosting.host.createdefaultbuilder?view=dotnet-plat-ext-6.0#Microsoft_Extensions_Hosting_Host_CreateDefaultBuilder_System_String___) settings. Here are the expected parameters:

| Variable | Purpose |
| - | - |
| AZURE_SUBSCRIPTION_ID | Subscription ID of the APIM instance to be updated |
| AZURE_RESOURCE_GROUP_NAME | Resource group name of the APIM instance to be updated |
| API_MANAGEMENT_SERVICE_OUTPUT_FOLDER_PATH | Folder where the APIM artifacts are located |
| AZURE_BEARER_TOKEN | Token for authentication to Azure. If this is not specified, the tool authenticate with  the [``DefaultAzureCredential``](https://docs.microsoft.com/en-us/dotnet/api/azure.identity.defaultazurecredential?view=azure-dotnet). |
| API_MANAGEMENT_SERVICE_NAME  | Name of the APIM instance to publish to. This can also be parsed from the configuration file |
| CONFIGURATION_YAML_PATH | Path to the Yaml configuration file used to override different configurations (e.g. policy backend value,  namevalue pairs, just to name a few) when promoting across APIM environments (e.g. dev -> qa -> prod). You will need a unique Yaml configuration file per environment  (e.g. configuration.prod.yaml for production) when overriding configurations across environments. More on this later on in the lab.  |
| COMMIT_ID | Git commit ID. If specified, the tool will only use files that were affected by that commit. New/modified files will be updated in Azure, and deleted artifacts will be removed from the Azure APIM instance. If unspecified, the tool will do a Put operation on the Azure APIM instance with all files in the artifacts folder. |

### Configuration Override Across Environments
 In an enterprise setting you may want to override some configurations as you promote your APIM across environments. For example you may have a policy which points to a backend url which is different across environments. Or you may be using a completely different application insights instance across environments and you would like to point to the correct application insights instance. In order to override these configurations you will need to provide them inside a environment specific configuration file which the publisher tool can pick up and parse when pushing the changes across different APIM instances. For example if you have three different environments (Dev -> QA -> Prod) then you would have two separate configuration files (e.g. configuration.qa.yaml and configuration.prod.yaml). The lowest environment doesn't require a configuration file as its the source environment.
 
Here is a [**sample configuration file**](https://github.com/Azure/apiops/blob/main/configuration.prod.yaml). The image below shows how the aforementioned sample configuration file maps to the generated artifacts.

![configuration Overrides](../../assets/images/Yaml_configuration.png)

 
Note that the configuration file is optional. In addition the different properties listed in the table below are optional as well. For example if you only need to override a nameValue then you would only include the namedValues property.

Below is the full list of supported configuration overrides that the publisher tool supports. 

| Property | Purpose |
| - | - |
| apimServiceName | Name of the destination APIM instance that you would like to promote to. Note that if you provide both the apimServiceName and the API_MANAGEMENT_SERVICE_NAME environment variable then the configuration file will take precedence    |
| namedValues | List of named value pairs to override. All three types (Plain - Secret - Key Vault) are supported
| loggers | Information for the application insights instance to utilize in the destination environment APIM instance |
| diagnostics | Configuration for the verbosity setting of the application insights instance to utilize in the destination environment APIM instance  |
| apis | list of apis for you which you would like to override settings like the application insights etc.. If you are utilizing versioning/revisioning in APIM then you need to set the target api version & revision to apply application insights to e.g. 'my-api', 'my-api-v2', 'my-api-v2;rev=2' |

As mentioned above the publisher supports overriding secret named values. Whereas the publisher supports both types of APIM secrets (secret and Azure Key Vault), we recommend using Azure Key Vault whenever possible. 

If you are trying to override a secret stored in Azure Key Vault then you can simply override the named value in your configuration file as demonstrated in the following [**sample configuration file**](https://github.com/Azure/apiops/blob/main/configuration.prod.yaml).

Also when using Key vault make sure you complete the steps below. You can either carry them ahead of time or at the time of creating the Key Vault named value within your APIM instance. You need to carry the steps below on every APIM instance (QA, PROD, etc.) to which you will be promoting to as infrastructure activities are outside the scope of the APIOPS tool.


![configuration Overrides](../../assets/images/APIM-keyvault-access.png)



```
Note: You don't have to create the named values in the target APIM environments ahead of time as they will be created by the publisher.
```

Docs and implementation for supporting the secret named value type is Under Construction. Come back soon. 🚧
