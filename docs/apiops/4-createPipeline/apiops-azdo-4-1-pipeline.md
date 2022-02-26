---
title: Create pipeline to automatically push changes
parent: Create APIOps pipeline
has_children: false
nav_order: 1
---


### Create pipeline to automatically push changes using creator tool

1. In your **apim-tools** repository, update [**tools/pipelines/run-creator.yaml**](https://github.com/Azure/apiops/tree/main/tools/pipelines/run-creator.yaml) with the following settings:
    - Line 5: Set the repository value to your APIM artifacts repository name. In this hands-on-lab, we're using **apim-artifacts**.
    - Line 7: Set the repository value to your APIM artifacts repository name. In this hands-on-lab, we're using **apim-artifacts**.
    - Line 11: Replace **main** with the main branch name of your artifacts repository.
    - Line 28: Set the checkout value to your APIM artifacts repository name. In this hands-on-lab, we're using **apim-artifacts**.
2. Create a new pipeline based on [**run-creator.yaml**](https://github.com/Azure/apiops/tree/main/tools/pipelines/run-creator.yaml) in your **apim-tools** repository.
> If this is your first time running this pipeline, you may be prompted to [authorize access](https://docs.microsoft.com/en-us/azure/devops/pipelines/repos/multi-repo-checkout?view=azure-devops#why-am-i-am-prompted-to-authorize-resources-the-first-time-i-try-to-check-out-a-different-repository) to the **apim-tools** repository.