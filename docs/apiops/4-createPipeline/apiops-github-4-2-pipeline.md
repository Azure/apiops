---
title: Create pipeline to automatically push changes
parent: Create APIOps pipeline
has_children: false
nav_order: 2
---


### Create pipeline to automatically push changes using creator tool

1. Update [**.github/workflows/run-creator.yml**](https://github.com/Azure/apiops/blob/main/.github/workflows/run-creator.yaml) with the following settings:
    - Line 15: Set the resource group environment variable to the resource group within which your Apim instance lives.
    - Line 16:  Set the APIM environment variable to the name of your  Apim instance.
2. Head back to the **Pull requests** section on your github repo. Review the changes, and once you're happy with them, complete the PR. If you head to the Actions section on our Gtihub repo you should now see the Run-Creator workflow triggered. Once the workflow run is successful head to your Azure portal to confirm that the APIM instance reflects your changes.