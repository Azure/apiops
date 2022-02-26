---
title: Create APIM artifacts in GitHub from extractor tool
parent: Create APIM artifacts
has_children: false
nav_order: 2
---


## Create APIM artifacts in GitHub from extractor tool


1. Update [**.github/workflows/run-extractor.yml**](https://github.com/Azure/apiops/blob/main/.github/workflows/run-extractor.yaml) with the following settings:
    - Line 11: Set the resource group environment variable to the resource group within which your Apim instance lives.
    - Line 12:  Set the APIM environment variable to the name of your  Apim instance.
2. Next head to the actions section within your repository and manually run the  "Run - Extractor" workflow. ![pipeline variable group](../../assets/images/GithubActionsRunExtractor.png)

3. Under **Pull requests** section on your github repo, you should see a new pull request with the extracted artifacts. Don't merge the Pull Request yet as we still need to configure the Run-Creator.yml file in the next section before we merge. You can always rerun the pipeline to refresh your repository with the latest Azure changes.
![create-pr-extractor](../../assets/images/PullRequest.png) 
    


