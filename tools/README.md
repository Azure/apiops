# Debug Instructions using Codespaces

## Creating new Codespace instance

* To learn more about Codespaces, go to [GitHub Codespaces Documentation - GitHub Docs](https://docs.github.com/en/codespaces).
* Go to the GitHub repository for this Lab: [apiops](https://github.com/Azure/apiops)
* Click the `Code` button on this repo
  * Select `Codespaces` tab, next to `Local`
  * If you don't see `Codespaces` tab, and if you are a Microsoft employee, you will need to first [link your Microsoft alias to your GitHub account](https://docs.opensource.microsoft.com/github/accounts/linking/)
* Click `New codespace`
  * Choose the `2 core` option

## Debugging .Net Applications in Codespace

* Log in to Azure from a bash or zsh terminal via: `az login --use-device-code`
* Generate deployment credentials:  

```bash
az ad sp create-for-rbac --name myApp --role contributor --scopes /subscriptions/{subscription-id}/resourceGroups/exampleRG --sdk-auth
```

* The output is a JSON object with the role assignment credentials. Copy this JSON object for later. You'll only need the sections with the clientId, clientSecret, subscriptionId, and tenantId values.
* Install `C# for Visual Studio Code` extension
* Open [Extractor.cs](tools/code/extractor/Extractor.cs) file (or any other source code file)
* Go to `VS Code Run & Debug` tab
* Click `Generate C# Assets for Build & Debug` option
* Open `launch.json` file
* Add the `env` field to the `configurations` section of `.NET Core Launch (console)`, as shown below:

```json
"configurations": [
    "name": ".NET Core Launch (console)"
    ...
    “env”: {
                “AZURE_RESOURCE_GROUP_NAME”: “apiops-rg”,
                “API_MANAGEMENT_SERVICE_NAME”: “apiops-dev”,
                “AZURE_CLIENT_ID”: “d36cfc27-3a45-45f0-b75c-59eb23993f49”,
                “AZURE_CLIENT_SECRET”: “ISx8DU.nUsahajsx3dZlNFV6Tem_mddkQz”,
                “AZURE_TENANT_ID”: “72f988bf-86f1-41af-91ab-2d7cd011db47”,
                “AZURE_SUBSCRIPTION_ID”: “0005c093-d9ad-44b4-abe8-f507848419ca”,
                “API_MANAGEMENT_SERVICE_OUTPUT_FOLDER_PATH”: “/tmp/apiops-extractor”
            }
]
```

* Place a breakpoint on the opened source code file
* press F5 to start debugging