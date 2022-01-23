---
title: Azure Devops + Apim Devops Kit
parent: DevOps
has_children: false
nav_order: 1
---


Please see [aka.ms/apimdevops](http://aka.ms/apimdevops) for more guidance and tools around automating deployment across multiple API Management environments.

# Continuos Integration and Continuos Deployment using Azure DevOps

The following instructions demonstrate how to deploy the contents of this example repository using Azure DevOps Repos and Azure DevOps Pipelines.

## Pre-reqs

There are two options to execute te apim devops kit, locally with .net and azure cli or using the azure cloude shell. Please select your prefered option:


Local
- [Dotnet core 3.1](https://dotnet.microsoft.com/download) Installed in your local machine
- [Azure CLI](https://docs.microsoft.com/en-us/cli/azure/install-azure-cli) installed on your local machine 

Azure Cloud Shell
- [How to enable Cloudeshell](https://newhelptech.wordpress.com/2019/05/18/step-by-step-how-to-enable-azure-cloud-shell-in-microsoft-azure/)

Optional(if you want to deploy with a build pipeline):

- Azure DevOps Account
- An Azure DevOps Repo configured ([how to configure an Azure DevOps Repo](https://docs.microsoft.com/en-us/azure/devops/repos/get-started/sign-up-invite-teammates?view=azure-devops))


## Architecture

![](../../assets/images/apim-devops-architecture.png)

This is one example of how to use Azure DevOps Repo and Azure DevOps Pipelines to enable a Continuous Integration and Continuous Deployment (CI/CD) for API\'s on an Azure API Management Service. There're other ways to do this, but this is a \"Keep it Simple\" to help you start. (Planning to have another version using Github and Github Actions soon).

This is our Development API management. In general, developers will create their API\'s in one instance of APIM to test it.

Create your API's on API Management Development environment. ([How to create API's on API Management](https://docs.microsoft.com/en-us/azure/api-management/import-and-publish))

![](../../assets/images/apim-dev.png)

## APIM DevOps Toolkit

**Running the Extractor**

After a developer creates and tests APIs in API management, it is time to extract those APIs using this DevOps Resource Kit.

Below are the steps to run the extractor from source code:

- Clone this repository and navigate to {path\_to\_folder}/src/APIM\_ARMTemplate/apimtemplate
    ```bash
    git clone https://github.com/Azure/azure-api-management-devops-resource-kit.git
    cd src/APIM_ARMTemplate/apimtemplate
    ```
- Restore its packages using

    ```bash
    dotnet restore 
    ```

- Make sure you have signed in using Azure CLI and have switched to the subscription containing the API Management instance from which the configurations will be extracted. Ensure you are using the correct Azure subscription: 

   ```bash
   # View subscriptions
   az account list
   ```

   ```bash
   # Verify selected subscription
   az account show
   ```

   ```bash
   # Set correct subscription (if needed)
   az account set --subscription <subscription_id>

   # Verify correct subscription is now set
   az account show
   ```


**Extractor Arguments**

You have two choices when specifying your settings:

- By using a JSON file with key-values where the keys matches the table below. Use the `extractorConfig` argument:

    `extract --extractorConfig c:/temp/extractSettings.json` 

- Or you can also pass the arguments on the command line. For instance 
    `extract --sourceApimName my-feature-apim --destinationApimName company-stable-apim --resourceGroup my-feature-rg --fileFolder c:\\temp\\apim-extract --apiName MyFeatureV1Api`


    Where:

    **sourceApimName:** API Management where you created your API

    **destinationApimName:** It\'s just a convention to set the DESTINATION-API-NAME in front of the generated files.

    **resourceGroup:** Resource group where the DEV-APIM-NAME is hosted.

    **fileFolder:**  <your_clone_devops_respository_filespec>  (NOTE: git clone and create the local repos before executing the dotnet run extract) 

    ***For more information on how to run the application and parameters, [go to this page](https://github.com/Azure/azure-api-management-devops-resource-kit/blob/master/src/APIM_ARMTemplate/README.md#extractor).***

- For this example, we will pass the arguments on the command line.  So, run the application with:

    ```bash
    dotnet run extract --sourceApimName <YOURAPIMINSTANCE> --resourceGroup <YOURAPIMRESOURCEGROUP> --destinationApimName <NEWAPIMINSTANCE>  --fileFolder apim-extract
    ```

    i.e.
    ```bash
    dotnet run extract --sourceApimName famc-apimlab2 --resourceGroup famc-apimlab2 --destinationApimName famc-apimlab2-prod  --fileFolder apim-extract 
    ```



After executing the command above, you will see something similar to this:

![](../../assets/images/apim-extractor.png)

Then you see the JSON files extracted:

![](../../assets/images/extracted-files.png)

Now, push them to your Azure DevOps Repo

```
git add *
git commit -a -m "add extracted *.json files to devops repository
git push
```

# Build Pipeline

We will use a **Build Pipeline** to pull the extracted files from a repo and put it on Azure DevOps Artefacts Folder. 
 
1 - Add these tasks to your build:
- Get Sources
- Copy Publish Artifacts

2 - Configure the fields as show bellow:

![A screenshot of a cell phone Description automatically
generated](../../assets/images/ado-build-pipeline1.png)

![A screenshot of a cell phone Description automatically
generated](../../assets/images/ado-build-pipeline2.png)

# Release Pipeline

You can use [this](https://docs.microsoft.com/en-us/azure/devops/pipelines/release/?view=azure-devops) document as a reference on how to create a release pipeline.

1 - Add an [**Azure Deployment tasks**](https://docs.microsoft.com/en-us/azure/devops/pipelines/tasks/deploy/azure-resource-group-deployment?view=azure-devops) for each file generated by the extractor, following the order:

```
- Products.template.json
- Tags.template.json
- namedValues.template.json
- apis.template.json
- globalServicePolice.template.json
```

2 - Set "Override template parameters" field with "-ApimServiceName \<QA-APIM-NAME>\"

3 - Set set [Deployment mode to Incremental](https://docs.microsoft.com/en-us/azure/azure-resource-manager/templates/deployment-tutorial-pipeline#create-a-devops-project)

![](../../assets/images/ado-release-qa-parameters.png)

When you finish the steps above, you will see something like this:

![](../../assets/images/ado-deploy-qa.png)

Clone your QA environment and update the **Resource Group** and **ApimServiceName** value on "Override template parameters" field to reflect your "production" APIM.

![](../../assets/images/ado-release-prod.png)

Now you will see your Release Pipeline like this:

![](../../assets/images/ado-release-pipeline.png)

By using a combination of manual deployment approvals, gates, and manual intervention within a release pipeline in Azure Pipelines, you can quickly and easily configure a release pipeline with all the control and auditing capabilities you require for your DevOps CI/CD processes. You will find more information on this [link](https://docs.microsoft.com/en-us/azure/devops/pipelines/release/deploy-using-approvals?view=azure-devops). 


