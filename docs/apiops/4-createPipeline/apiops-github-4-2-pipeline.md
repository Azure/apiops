---
title: Make APIM changes via code
parent: Create APIOps pipeline
has_children: false
nav_order: 2
---


### Create pipeline to automatically push changes using creator tool

1. Go back to the PR that was created in the previous step as a result of running the extractor. Once the PR is merged the run-creator pipeline should automatically trigger. Remember that the creator pipeline requires manual approval before promoting between stages. To promote to the prod environment, wait on the dev stage to succeed and then click on the "Review deployments" button and approve to deploy the changes to the prod environment.
![approve promotion to prod environment](../../assets/images/github_promotion.png) 
2. Approve the prod environment for deployement.![approve promotion to prod environment](../../assets/images/github_promotion_approval.png) 
