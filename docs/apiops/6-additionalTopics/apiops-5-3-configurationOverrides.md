---
title: Configuration Override
parent: Additional Topics
has_children: false
nav_order: 3
---


## Configuration Overrides Across Environments
 In an enterprise setting you may want to override some configurations as you move them between environments. For example you may have a policy which points to a backend url which is different across environments. Or you may be using a completely different application insights instance across environemnts. In order to override these configurations you will need to provide the overrides inside a environment specific configuration file which the creator tool can pick up and parse when pushing the changes across different APIM instances. For example if you have three different environments (Dev -> QA -> Prod) then you would have two separate configuration files (e.g. configuration.qa.yaml and configuration.prod.yaml). Here is a [**sample configuration file**](../../../configuration.prod.yaml) which allows you to override a name value called environment before promoting it to the production environment. This way if you have some policies that you want them to be environment specific you can simply reference the environment within your policy xml files. Below is the full list of supported configuration overrides that the creator tool supports. 

| Configuration | Purpose |
| - | - |
| apimServiceName | Name of the destination APIM instance   |
| namedValues | List of named value pairs to override |
| loggers | Information for the application insights instance to utilize in the destination environment APIM instance |
| diagnostics | Configuration for the verbosity setting of the application insights instance to utilize in the destination environment APIM instance  |
| apis | list of apis for you which you would like to override the application insights settings |
