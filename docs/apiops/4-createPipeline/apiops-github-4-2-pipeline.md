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
3. At this point you have successfully extracted the different apim artifacts and promoted them across different environments. But in an enterprise setting you may want to override some configurations as you move them between environments. For example you may have a policy which points to a backend url which is different across environments. Or you may be using a completely different application insights instance across environemnts. In order to override these configurations you will need to provide the overrides inside a environment specific configuration file which the creator tool can pick up and parse when pushing the changes across different APIM instances. For example if you have three different environments (Dev -> QA -> Prod) then you would have two separate configuration files (e.g. configuration.qa.yaml and configuration.prod.yaml). Here is a [**sample configuration file**](../../../configuration.prod.yaml) which allows you to override a name value called environment before promoting it to the production environment. This way if you have some policies that you want them to be environment specific you can simply reference the environment within your policy xml files. For a full list of supported configuration overrides please refer to the [**additional topics section**](../6-additionalTopics/apiops-5-3-configurationOverrides.md).
4. Finally if you were to peek into [**run-creator.yaml**](https://github.com/Azure/apiops/tree/main/.github/workflows/run-creator.yaml) you will notice that the creator tool expects a CONFIGURATION_YAML_PATH. As explained earlier in the apim tools section CONFIGURATION_YAML_PATH is used to provide the creator tool with a path to the Yaml configuration file used to override different configurations when promoting across APIM environments. You will need a unique Yaml configuration file per environment  (e.g. configuration.prod.yaml for production).
