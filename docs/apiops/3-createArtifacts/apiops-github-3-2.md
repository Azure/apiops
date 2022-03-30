---
title: Create APIM artifacts in GitHub from extractor tool
parent: Create APIM artifacts
has_children: false
nav_order: 2
---


## Create APIM artifacts in GitHub from extractor tool


1. Head to the actions section within your repository and manually run the  "Run - Extractor" workflow. ![pipeline variable group](../../assets/images/GithubActionsRunExtractor.png)

3. Under **Pull requests** section on your github repo, you should see a new pull request with the extracted artifacts. Don't merge the Pull Request yet as we still need to configure the Run-Creator.yml file in the next section before we merge. You can always rerun the pipeline to refresh your repository with the latest Azure changes.
![create-pr-extractor](../../assets/images/PullRequest.png) 
    


