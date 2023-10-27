---
title: Repo Contents
parent: Additional Topics
has_children: false
nav_order: 1
---


| Path | Purpose |
| - | - |
| tools/azdo_pipelines/| Azure Devops pipelines for running the extractor and publisher tools
| tools/.github/workflows/ | Github actions for running the extractor and publisher tools |
| tools/code | Source code for the extractor and publisher tools |
| tools/scripts/New-ExtractorConfiguration.ps1 | Optional script generate new extractor configurations as well as generating configs of existing APIOps instances with new content added which allows you to diff the new and old extractor configs |
| configuration.extractor.yaml | A sample yaml extractor configuration file to signal to the extractor to extract select apis, backends, products, tags, loggers, diagnostics, subscriptions, namedvalues, and policy fragments. This is an optional parameter and will only come into play if you want different teams to manage different apis, tags, etc.. You typically will have one configuration per team. Note: You can call the file whatever you want as long as you reference the right file within your extractor pipeline. On a side note since the introduction of Workspaces we believe they are the better solution compared to using this file, but we are keeping the support as not everyone will be able to use workspaces |
| configuration.[env].yaml | A sample yaml publisher configuration file to override configuration when running the publisher to promote across different environments (e.g. configuration.prod.yaml for prod environment). Although its optional parameter, you are expected to provide a unique file for each environment as usually different environments have different values (e.g. namevalue). For example if you have a QA environment you would provide another file called configuration.qa.yaml which would have qa specific configuration. Note: You can call the file whatever you want as long as you reference the right file within your publisher pipeline. Also note that if you don't pass the target instance name in the pipelines themselves then you have to pass it as part of the configuration file itself |
| sample-artifacts-folder | Sample output from the extractor tool. The publisher tool expects this structure and can automatically push changes back to Azure |