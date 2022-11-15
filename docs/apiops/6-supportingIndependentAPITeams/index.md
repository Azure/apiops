---
title: Supporting Independent API Teams
has_children: false
nav_order: 7
---
# Supporting Independent API Teams
So far all the guidance has made the assumption that all apis will be managed by a centralized team and thus all apis will be extracted into a single repository. In this section we will discuss the detailed involved in setting up a decentralized apim instance. Basically, the scenario involves having different teams managing different apis within an APIM instance.
## Extracting Select APIs
The extractor tool supports extracting select apis which enables the following scenarios:
- Enabling different teams to work with different set of apis. For example team A can work on api1 and api2 while team B can work on api3 and api4
- Enables developers to experiment with an api on the azure portal without extracting it the next time the extractor tool runs. This allows the development team to safely experiment with apis in the Azure portal before its ready to be promoted to higher environments

In order to signal to the extractor tool to extract select apis you need to provide a configuration file (either yaml or json based) which includes the list of apis to extract. Following the example above you can instruct the extractor tool to extract api1 and api2 by providing the following configuration file:

```yaml
apiNames:
  - api1
  - api2
```
Here is the Azure Devops extractor pipeline windows offering either full extraction or selective api extraction through the configuration file: <br />
![extractor pipeline](../../assets/images/Extractor_Configuration.png)



Here is a link to a sample  [**extractor configuration file**](https://github.com/Azure/apiops/blob/main/configuration.extractor.yaml).



If the extractor successfully picks up the configuration file then you will notice that apis folder will only include the apis specified in the configuration file.

## DevOps Process For Supporting Independent API Teams
Although each APIM DevOps team will have their own internal process, we would like to recommend a baseline approach for supporting independent API teams. This is by no means the only viable approach and thus you are free to setup your own DevOps process that best suits your organizational needs.

We envision having an operators team who will be responsible for APIM instance configurations (name/values, diagnostics settings, global policies, etc.) as well as different teams who will be exclusively focused on developing a subset of apis. In the setup below we can see three different teams with each team extracting the artifacts to their own repo with the artifacts of interest in the red box. Now we recommend that you add a .gitignore file to each repo to ignore the files that are not of interest in the team to avoid accidental change of the files belonging to other teams.

![extractor pipeline](../../assets/images/Multi_API_Team_Devops.png)

## Controlling Changes to Specific APIs
Whereas the two previous sections offered options to either extract specific apis or use different repositories for different apis, there may be situations where a team prefers to have a mon-repo approach where all the apis are extracted into a single repository. In this case we recommend you look into limiting changes against certain apis to specific individuals. This can be achieved in both Github as well Azure Devops. In Github you can use a [CODEOWNERS](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/about-code-owners) file to define individuals or teams that are responsible for specific apis in a repository. As for Azure Devops you can (automatically add reviewers)[https://learn.microsoft.com/en-us/azure/devops/repos/git/branch-policies?view=azure-devops&tabs=browser#automatically-include-code-reviewers] to pull requests that change files in specific directories and files, or to all pull requests in a repo.