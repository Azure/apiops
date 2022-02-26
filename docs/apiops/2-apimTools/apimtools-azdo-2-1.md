---
title: Configure APIM tools in Azure DevOps
parent: Configure APIM tools
has_children: false
nav_order: 1
---


## Configure APIM tools in Azure DevOps

1. Create a new project in Azure DevOps for this tutorial (optional).
2. Create a new repository to hold the tools code. We will refer to it as **apim-tools** in this tutorial.
3. Copy the [**tools**](https://github.com/Azure/apiops/tree/main/tools) folder  to this new repository. Your folder structure should look like this:
    - your-repo-name
        - tools
            - code
                - ...
            - pipelines
                - ...
            - utils
4. [Create an Azure Artifacts feed](https://docs.microsoft.com/en-us/azure/devops/artifacts/concepts/feeds?view=azure-devops#create-a-feed). We will use the name **apim-tools** in this tutorial.
![artifacts_feed](../../assets/images/artifacts_feed.png)
5. [Create a pipeline variable group](https://docs.microsoft.com/en-us/azure/devops/pipelines/library/variable-groups?view=azure-devops&tabs=classic#create-a-variable-group) called **apim-automation**. In that group, add these variables:
    - **ARTIFACTS_FEED_NAME** and for its value, enter the name of the artifacts feed you just created.
    - **SERVICE_CONNECTION_NAME** and for its value, enter the name of your [Azure service connection](https://docs.microsoft.com/en-us/azure/devops/pipelines/library/service-endpoints?view=azure-devops&tabs=yaml).
    - **RESOURCE_GROUP_NAME** and for its value, enter the resource group name of your Azure APIM instance.
![pipeline variable group](../../assets/images/variable_groups.png)
6. Create a new pipeline based on [**publish-extractor.yaml**](https://github.com/Azure/apiops/tree/main/tools/pipelines/publish-extractor.yaml). This pipeline will compile the extractor tool whenever it's updated and publish it as a package in Azure DevOps Artifacts.
![extractor pipeline](../../assets/images/extractor_pipeline.png)
7. Run the pipeline.
8. Create a new pipeline based on [**publish-creator.yaml**](https://github.com/Azure/apiops/tree/main/tools/pipelines/publish-creator.yaml). This pipeline will compile the creator tool whenever it's updated and publish it as a package in Azure DevOps Artifacts.
9. Run the pipeline.