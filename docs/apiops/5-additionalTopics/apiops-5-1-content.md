---
title: Repo Contents
parent: Additional Topics
has_children: false
nav_order: 1
---


| Path | Purpose |
| - | - |
| sample-artifacts-folder | Sample output from the extractor tool. The creator tool expects this structure and can automatically push changes back to Azure. |
| tools/code | Source code for the extractor and creator tools
| tools/pipelines/publish-creator.yaml | Azure DevOps pipelines YAML to compile the creator tool and publish it as a package in an Azure Artifacts feed. |
| tools/pipelines/publish-extractor.yaml | Azure DevOps pipelines YAML to compile the creator tool and publish it as a package in an Azure Artifacts feed. |
| tools/run-creator.yaml | Azure DevOps pipelines YAML to push artifact changes to Azure using the creator tool |
| tools/run-extractor.yaml | Azure DevOps pipelines YAML to generate artifacts from an existing APIM instance. |
