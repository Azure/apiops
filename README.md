# Project

This project contains sample code for doing APIOps with Azure API Management.

# Tutorial
In this scenario, we have an existing Azure API management instance that we want to start managing via code. We will use the contents of this repository to do so. Please note that the steps below assume familarity with Azure API Management and Azure DevOps.

## Prerequisites
- An Azure subscription where you can create/modify APIM instances
- An Azure DevOps instance with a service connection to the Azure subscription.

## Steps
### Configure APIM tools in Azure DevOps
1. Create a new project in Azure DevOps for this tutorial (optional).
2. Create a new repository to hold the tools code. We will refer to it as **apim-tools** in this tutorial.
3. Publish the contents of the [**tools**](tools) folder to this new repository. Your folder structure should look like:
    - your-repo-name
        - code
            - ...
        - pipelines
            - ...
        - utils
4. [Create an Azure Artifacts feed](https://docs.microsoft.com/en-us/azure/devops/artifacts/concepts/feeds?view=azure-devops#create-a-feed). We will use the name **apim-tools** in this tutorial.
5. [Create a pipeline variable group](https://docs.microsoft.com/en-us/azure/devops/pipelines/library/variable-groups?view=azure-devops&tabs=classic#create-a-variable-group) called **apim-automation**. In that group, add these variables:
    - **ARTIFACTS_FEED_NAME** and for its value, enter the name of the artifacts feed you just created.
    - **SERVICE_CONNECTION_NAME** and for its value, enter the name of your Azure service connection.
    - **RESOURCE_GROUP_NAME** and for its value, enter the resource group name of your Azure APIM instance.
6. Create a new pipeline based on [**publish-extractor.yaml**](tools/pipelines/publish-extractor.yaml). This pipeline will compile the creator tool whenever it's updated and publish it as a package in Azure DevOps Artifacts.
7. Run the pipeline.
8. Create a new pipeline based on [**publish-creator.yaml**](tools/pipelines/publish-creator.yaml). This pipeline will compile the creator tool whenever it's updated and publish it as a package in Azure DevOps Artifacts.
9. Run the pipeline.

### Create a sample APIM instance in Azure
1. In the Azure portal, search for **API Management services**.
2. Click on **Create**, and go through the wizard to create an instance. We recommend using the **Consumption** tier for cost-effectiveness.
3. Once the instance is created, create a few APIs and operations. You can use the [PetStore OpenAPI definition](https://raw.githubusercontent.com/OAI/OpenAPI-Specification/main/examples/v3.0/petstore.yaml) as a starting point. To do so, go to your instance, select **APIs**, select **OpenAPI** under **Create from definition**, and enter the [PetStore OpenAPI URL](https://raw.githubusercontent.com/OAI/OpenAPI-Specification/main/examples/v3.0/petstore.yaml). Click on **Create** when you're done.

Now that we have an existing instance, let's generate artifacts that can be maintained via code.

### Create APIM instance artifacts with the extractor tool
1. Create a new repository in Azure DevOps to hold the instance artifacts. We'll refer to this as **apim-artifacts** in this tutorial.
2. Give the Azure DevOps build service permissions on this repository. To do so, go to **Project settings** -> **Repositories**. Select your repository, click on the **Security** tab, and under **Users** you should see a user called **[project-name] Build Service ([organization-name])**. Select it, then set the following permissions to **Allow**:
    - Contribute
    - Contribute to pull requests
    - Create branch
3. Make sure the Azure DevOps service principal has read access to your Azure APIM instance.
4. Create a new pipeline based on [**run-extractor.yaml**](tools/pipelines/run-extractor.yaml) in your **apim-tools** repository.
5. Run the pipeline. When prompted for parameters, specify the instance name, resource group name, the APIM artifacts repository name (**apim-artifacts** in this tutorial), and the main branch where artifacts will live. The pipeline will use the extractor tool to download instance artifacts, then it will create a pull request to the main branch of your APIM artifacts repository.
> If this is your first time running this pipeline, you may be prompted to [authorize access](https://docs.microsoft.com/en-us/azure/devops/pipelines/repos/multi-repo-checkout?view=azure-devops#why-am-i-am-prompted-to-authorize-resources-the-first-time-i-try-to-check-out-a-different-repository) to the **apim-tools** repository.
6. Go to your **apim-artifacts** repo. Under **Pull requests** -> **Active**, you should see a new pull request with the extracted artifacts. Review the changes, and once you're happy with them, complete the PR.

You can always rerun the pipeline to refresh your repository with the latest Azure changes. Now that we have artifacts in our repository, let's create a pipeline to push changes back to Azure.

### Create pipeline to automatically push changes with the creator tool
1. In your **apim-tools** repository, update [**tools/pipelines/run-creator.yaml**](tools/pipelines/run-creator.yaml) with the following settings:
    - Line 5: Set the repository value to your APIM artifacts repository name. In this tutorial, we're using **apim-artifacts**.
    - Line 7: Set the repository value to your APIM artifacts repository name. In this tutorial, we're using **apim-artifacts**.
    - Line 11: Replace **main** with the main branch name of your artifacts repository.
    - Line 28: Set the checkout value to your APIM artifacts repository name. In this tutorial, we're using **apim-artifacts**.
2. Create a new pipeline based on [**run-creator.yaml**](tools/pipelines/run-creator.yaml) in your **apim-tools** repository.
> If this is your first time running this pipeline, you may be prompted to [authorize access](https://docs.microsoft.com/en-us/azure/devops/pipelines/repos/multi-repo-checkout?view=azure-devops#why-am-i-am-prompted-to-authorize-resources-the-first-time-i-try-to-check-out-a-different-repository) to the **apim-tools** repository.

### Make APIM changes via code
1. In your **apim-artifacts** repository, go to **apis/``pick_an_api``/apiInformation.json**. Change the display name to something else and commit your changes.
2. In Azure DevOps, go to **Pipelines**. You should see your **run-creator** pipeline running automatically.
3. Once the pipeline is complete, go to the Azure portal and find your APIM instance. Under **APIs**, you should see that your API name has been updated.
4. In your **apim-artifacts** repository, delete the **apis** folder and commit your changes. Once the triggered pipeline completes, go back to the Azure portal and find your APIM instance. Under **APIs**, all your APIs should be gone. You may need to force a refresh of the Azure portal by pressing **Ctrl+F5** to see the latest changes.


# Contents
| Path | Purpose |
| - | - |
| sample-artifacts-folder | Sample output from the extractor tool. The creator tool expects this structure and can automatically push changes back to Azure. |
| tools/code | Source code for the extractor and creator tools
| tools/pipelines/publish-creator.yaml | Azure DevOps pipelines YAML to compile the creator tool and publish it as a package in an Azure Artifacts feed. |
| tools/pipelines/publish-extractor.yaml | Azure DevOps pipelines YAML to compile the creator tool and publish it as a package in an Azure Artifacts feed. |
| tools/run-creator.yaml | Azure DevOps pipelines YAML to push artifact changes to Azure using the creator tool |
| tools/run-extractor.yaml | Azure DevOps pipelines YAML to generate artifacts from an existing APIM instance. |
# Tools
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

# Trademarks

This project may contain trademarks or logos for projects, products, or services. Authorized use of Microsoft 
trademarks or logos is subject to and must follow 
[Microsoft's Trademark & Brand Guidelines](https://www.microsoft.com/en-us/legal/intellectualproperty/trademarks/usage/general).
Use of Microsoft trademarks or logos in modified versions of this project must not cause confusion or imply Microsoft sponsorship.
Any use of third-party trademarks or logos are subject to those third-party's policies.
