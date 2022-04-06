---
title: Repo Contents
parent: Additional Topics
has_children: false
nav_order: 1
---


| Path | Purpose |
| - | - |
| sample-artifacts-folder | Sample output from the extractor tool. The publisher tool expects this structure and can automatically push changes back to Azure. |
| tools/code | Source code for the extractor and publisher tools
| tools/pipelines/publish-publisher.yaml | Azure DevOps pipelines YAML to compile the publisher tool and publish it as a package in an Azure Artifacts feed. |
| tools/pipelines/publish-extractor.yaml | Azure DevOps pipelines YAML to compile the publisher tool and publish it as a package in an Azure Artifacts feed. |
| tools/run-publisher.yaml | Azure DevOps pipelines YAML to push artifact changes to Azure using the publisher tool |
| tools/run-extractor.yaml | Azure DevOps pipelines YAML to generate artifacts from an existing APIM instance. |
