---
title: Repo Contents
parent: Additional Topics
has_children: false
nav_order: 1
---


| Path | Purpose |
| - | - |
| .github/workflows/ | Github action workflows for publishing and running the extractor and publisher pipelines |
| sample-artifacts-folder | Sample output from the extractor tool. The publisher tool expects this structure and can automatically push changes back to Azure |
| tools/code | Source code for the extractor and publisher tools
| tools/pipelines/publish-publisher.yaml | Azure DevOps pipelines YAML to compile the publisher tool and publish it as a package in an Azure Artifacts fee. |
| tools/pipelines/publish-extractor.yaml | Azure DevOps pipelines YAML to compile the publisher tool and publish it as a package in an Azure Artifacts feed. |
| tools/run-publisher.yaml | Azure DevOps pipelines YAML to push artifact changes to Azure using the publisher tool |
| tools/run-extractor.yaml | Azure DevOps pipelines YAML to generate artifacts from an existing APIM instance |
| configuration.extractor.yaml | A sample yaml extractor configuration file to signal to the extractor to extract select apis. This is an optional parameter and will only come into play if you want different teams to manage different apis. You typically will have one configuration per team. Note: You can call the file whatever you want as long as you reference the right file within your extractor pipeline |
| configuration.prod.yaml | A sample yaml publisher configuration file to override configuration when running the publisher to promote across different environments. Although its optional parameter, you are expected to provide a unique file for each environment as usually different environments have different values (e.g. namevalue). For example if you have a QA environment you would provide another file called configuration.qa.yaml which would have qa specific configuration. Note: You can call the file whatever you want as long as you reference the right file within your publisher pipeline |
| tools/run-extractor.yaml | Azure DevOps pipelines YAML to generate artifacts from an existing APIM instance |
