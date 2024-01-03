---
title: Supported Scenarios
has_children: false
nav_order: 2
---


## Supported Scenarios

In this section we discuss the different scenarios that the Apiops tool supports. The tool supports two scenarios. The first scenario addresses the needs of users that build their APIs from the Azure portal. The second scenario addresses the needs of users who build their APIs via code utilizing an IDE (e.g. VS Code). Also please note that although we recommend utilizing the devops pipelines (both extractor and publisher pipelines) provided in this git repo, you are at liberty to utilize your own pipelines. The tools (both extractor and publisher) are independent of the provided pipelines and hence they could be triggered from within any pipeline. If you decide to go with your own devops pipelines you have to ensure that a proper process is in place. The rest of this lab assumes that you will be using the provided pipelines.

### Scenario A : Users Who Build APIs Using the Azure Portal

In this scenario the Operators and developers of the APIM instance prefer to use the Azure portal. The workflow looks like this: 

 
Carry all the changes in the API Portal -> Manually run the extractor pipeline within your Devops Environment (e.g. Github or Azure Devops) which will automatically create a PR including all the changes -> Manually approve and merge the PR into the main branch -> The merging process will automatically trigger the publisher pipeline which in turn will publish the changes to the current environment as well as the higher environments. There will be manual approvals when promoting across different environments to protect against accidental promotions.
 

There are couple gotchas that you need to be aware here:
- In this scenario you will utilize the extractor tool to generate the artifacts which will then be promoted across different environments using the publisher tool. 
- The provided publisher pipeline publishes the artifacts into the lower environment as well as the higher environments. Wheres republishing to the lower environment may seem repetitive (since the extraction is happening from that environment), this is done to ensure a consistent experience regardless of the scenario. Remember that in Scenario B everything is driven by code which means that you start in the IDE and would need to publish to all environments including the lowest one (e.g. dev environment). Also we felt that republishing to the environment from which you just extracted from serves as an additional guard rail before promoting to higher environments. You are at liberty to modify this behavior if you see fit.


### Scenario B : Users Who Build APIs Using an IDE

In this scenario the Operators and developers of the APIM instance prefer a code first approach. The workflow looks like this:

Create all the different artifacts from an IDE (e.g. VS Code) -> commit the changes to a dev branch -> manually create a PR to the main branch -> manually approve the PR and merge to the main branch -> the merging process will automatically trigger the publisher pipeline which in turn will publish the changes to the current environment as well as the higher environments. There will be manual approvals when promoting across different environments to protect against accidental promotions.. 


In this scenario you don't need to utilize the extractor tool to generate the artifacts as the artifacts will be created in the IDE. Having said that, nothing prevents you from utilizing the extractor tool to generate the initial artifacts if you are introducing the tool to an existing APIM instance.

# Supported  2022-09-01
Below there is a table with all [artifacts extracted and published by APIOps](https://learn.microsoft.com/rest/api/apimanagement/). 

|Operation Group|Description|Implemented in APIOps?|
|:----|:----|:----|
|API Diagnostic|Provides operations for managing Diagnostic settings for the logger in an API.|Yes|
|API Export|Exports an API to a SAS blob.|Yes|
|API Operation|Lists the operations for an API.|Yes|
|API Operation Policy|Provides operations for managing the policy configured at the API Operation Level of a service instance.|Yes|
|API Policy|Provides operations for managing the policy configured at the Api Level of a service instance.|Yes|
|API Product|Lists the APIs associated with a product.|Yes|
|API Revision|Lists the API revisions associated with an API in a service instance.|Yes|
|API Version Set|Provides operations for managing the Version Set of an API.|Yes|
|APIs|Provides operations for managing the APIs of a service instance.|Yes|
|Diagnostic|Provides operations for managing Diagnostic settings for the logger in a service instance.|Yes|
|Gateway|Provides operations for managing self-hosted gateways for a service instance.|Yes|
|Gateway Api|Provides operations for managing self-hosted gateway apis for a service instance.|Yes|
|Logger|Provides operations for managing Loggers used to collect events.|Yes|
|Named Value|Provides operations for creating and updating the named value collection for the service instance.|Yes|
|Operation|Provides API operations for managing operations for the service instance.|Yes|
|Policy|Provides operations for policy management at the global service instance level.|Yes|
|Product|Provides operations for managing products.|Yes|
|Product API|Provides operations for configuring which APIs associated with a product.|Yes|
|Product Policy|Provides operations for managing the policy configured at the Product Level of a service instance.|Yes|
| | | |
|Backend|Provides operations for managing Backends configured for the APIs|Yes|
|Policy Fragments| |Yes|
|API and Product Tags| |Yes|
|Group/Products Association| |Yes|
|GraphQL| |Yes|







