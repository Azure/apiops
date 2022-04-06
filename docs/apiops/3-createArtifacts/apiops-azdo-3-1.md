---
title: Create APIM artifacts in Azure DevOps from extractor tool
parent: Create APIM artifacts
has_children: false
nav_order: 1
---


## Create APIM artifacts in Azure DevOps from extractor tool

1. Make sure the Azure DevOps service principal has read access to your Azure APIM instance.
2. Create a new pipeline based on [**run-extractor.yaml**](https://github.com/Azure/apiops/blob/main/tools/pipelines/run-extractor.yaml) in your repository.
3. Run the pipeline. When prompted for parameters, specify the instance name, resource group name, the APIM artifacts repository name, folder where the artifacts will be extracted to, and the main branch where artifacts will live. The pipeline will use the extractor tool to download instance artifacts, then it will create a pull request to the main branch of your APIM artifacts repository.
4. Under **Pull requests** -> **Active**, you should see a new pull request with the extracted artifacts. **Don't merge the Pull Request yet** as we still need to configure the Run-publisher.yaml file in the next section before we merge.
![create-pr-extractor](../../assets/images/create-pr-extractor.png)

You can always rerun the pipeline to refresh your repository with the latest Azure changes. Now that we have artifacts in our repository, let's create a pipeline to push changes back to Azure.
