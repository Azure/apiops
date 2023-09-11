---
title: Publish APIM artifacts
has_children: true
nav_order: 6
---


## Publish APIM artifacts

This step guides you to automatically deploy changes to APIM when someone makes a change and creates a PR. When someone makes a PR in the Git repository, the API operators know they have new code to review. For example, when a developer takes the OpenAPI specification and builds the API implementation, they add this new code to the repository. The operators can review the PR and make sure that the API that's been submitted for review meets best practices and standards. Once the PR is approved this pipeline pushes and deploys the changes across the different APIM instances (e.g. dev -> qa -> prod).