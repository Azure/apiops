---
title: Configure APIM tools in Azure DevOps
parent: Configure APIM tools
has_children: false
nav_order: 3
---

> **Note**
> The instructions on this page pertain to new and simplified setup which was introduced with release v3.0.0
<br />
The older setup [can be found here](https://azure.github.io/apiops/apiops/3-apimTools/apimtools-github-2-4-old.html).

> **Note**
> Starting with release v4.0.0 both windows and linux build agents are supported. Thus you will notice that starting with v4.0.0 there are separate binaries (extractor and publisher) get generated for each OS.


## Configure APIM tools in Azure DevOps

1. Create a new project in Azure DevOps. We will refer to it as **apiops** in this tutorial
2. Head to the release you are targeting on the Gihub page. The list of releases can be found [here](https://github.com/Azure/apiops/releases). For this example we will assume you are trying to start with release v.3.0.0.  As you can see in the image below under the "Assets" section you have a file called **Azure_DevOps.zip**. Download that file and then extract the content into your repository. Your folder structure should look like:
    - your-repo-name
        - tools
            - pipelines
                - ...
            - utils

    ![Github_Release](../../assets/images/Github_Release_Azure_Devops.png)

    In order to update the pipelines in the future you will follow the same steps documented in step 2.

3. [Create a pipeline variable group](https://learn.microsoft.com/azure/devops/pipelines/library/variable-groups?view=azure-devops&tabs=classic#create-a-variable-group) called **apim-automation**. In that group, add these variables:
    - **SERVICE_CONNECTION_NAME** and for its value, enter the name of your [Azure service connection](https://learn.microsoft.com/azure/devops/pipelines/library/service-endpoints?view=azure-devops&tabs=yaml).
    - **APIM_NAME** and for its value, enter the name of lower environment apim instance name. You can optionally enter the **APIM_NAME** the higher environment if you have that information ready or you can enter it at a later point.
    - **RESOURCE_GROUP_NAME** and for its value, enter the resource group name of your Azure APIM instance. In this example we have two apim instances representing both the dev and prod environments so make sure you have two resource group entries representing both as shown in the image below.
    - **apiops_release_version** and for its value, enter the release number you would like to utilize. For example if you would like to utilize version 3 then you would set the value to **"v3.0.0"**. Its always recommended to utilize the latest release when possible as it usually includes new features and bug fixes. 
![pipeline variable group](../../assets/images/variable_groups_new.png)
4. Create a target [**environment**](https://learn.microsoft.com/azure/devops/pipelines/process/environments?view=azure-devops) called prod as shown below. The environment will allow us to require a manual approval between stages in a yaml based release pipeline. Choose Prod as the name and for the resource type choose None. ![prod environment](../../assets/images/ado_prod_environment.png)
5. After creating the environment add one ore more approvers by heading to the ellipses menu and click on "Approvals and checks" ![prod environment approvals](../../assets/images/ado_prod_environment_approvals.png)
6. Here we are adding a single approver but in an enterprise setting its recommended that you add two or more approvers. ![prod environment approver](../../assets/images/ado_prod_environment_approver.png)
7.  Quick note about running a pipeline. 
    > By default Azure DevOps build pipeline agents don't have enough permissions to perform some actions that are required for our pipeline. 
    > 1. To contribute to a repo, create a branch or update a pr. You need to grant that permission as discussed [here](https://learn.microsoft.com/azure/devops/pipelines/policies/set-permissions?toc=%2Fazure%2Fdevops%2Forganizations%2Fsecurity%2Ftoc.json&bc=%2Fazure%2Fdevops%2Forganizations%2Fsecurity%2Fbreadcrumb%2Ftoc.json&view=azure-devops)
    > 2. To contribute to artifact feed. You need to grant that permissions (contributor) as discussed [here](https://learn.microsoft.com/en-us/azure/devops/artifacts/feeds/feed-permissions?view=azure-devops#configure-feed-settings)
8. Thats it. You are now ready to extract and publish your Azure APIM instance artifacts. Refer to the extract and publish APIM artifacts sections for more information. For a list of supported artifacts refer to [this section ](https://azure.github.io/apiops/apiops/7-additionalTopics/apiops-7-3-supportedresources.html).
