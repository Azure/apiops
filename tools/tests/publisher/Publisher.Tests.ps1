BeforeDiscovery {
    $parameters = @{        
        testArtifactsPath = "C:\Users\user\source\repos\github\Azure\apiops\tools\tests\artifacts"
        extractorSourcePath = "C:\Users\user\source\repos\github\Azure\apiops\tools\code\extractor\extractor.csproj"
        publisherSourcePath = "C:\Users\user\source\repos\github\Azure\apiops\tools\code\publisher\publisher.csproj"
    }
}

Describe "Publisher tests" -ForEach $parameters {
    BeforeAll {
        Set-StrictMode -Version Latest
        $ErrorActionPreference = "Stop"
        $VerbosePreference = "Continue"
        $InformationPreference = "Continue"
    
        Write-Information "Creating resource group..."
        $resourceGroupParameters = @{
            Name     = "apiops-test-rg"
            Location = "eastus"
            Force    = $true
        }
        $resourceGroup = New-AzResourceGroup @resourceGroupParameters

        Write-Information "Creating APIM instance..."
        $apimInstanceParameters = @{
            "ResourceGroupName" = $resourceGroup.ResourceGroupName
            "Name"              = "apiopststr-apim"
            "Location"          = $resourceGroup.Location
            "Organization"      = "apiops"
            "AdminEmail"        = "admin@apiops.com"
            "Sku"               = "Consumption"
        }
        $apimInstance = New-AzApiManagement @apimInstanceParameters

        Write-Information "Compiling extractor..."
        $extractorOutputFolderPath = Join-Path $TestDrive "extractor"
        & dotnet publish $extractorSourcePath --self-contained --runtime "win-x64" -p:PublishSingleFile=true --output "$extractorOutputFolderPath"
        $extractorFilePath = Join-Path $extractorOutputFolderPath "extractor.exe"

        Write-Information "Compiling publisher..."
        $publisherOutputFolderPath = Join-Path $TestDrive "publisher"
        & dotnet publish $publisherSourcePath --self-contained --runtime "win-x64" -p:PublishSingleFile=true --output "$publisherOutputFolderPath"
        $publisherFilePath = Join-Path $publisherOutputFolderPath "publisher.exe"    
    
        Write-Information "Running publisher..."
        $env:AZURE_BEARER_TOKEN = (Get-AzAccessToken).Token
        $env:AZURE_SUBSCRIPTION_ID = (Get-AzContext).Subscription.Id
        $env:AZURE_RESOURCE_GROUP_NAME = $apimInstance.ResourceGroupName
        $env:API_MANAGEMENT_SERVICE_NAME = $apimInstance.Name            
        $env:API_MANAGEMENT_SERVICE_OUTPUT_FOLDER_PATH = $testArtifactsPath        
        $output = & $publisherFilePath
        if ($LASTEXITCODE -ne 0) {
            throw "Publisher failed. Output is $($output | Out-String)"
        }
            
        Write-Information "Running extractor..."
        $extractorArtifactsPath = Join-Path $TestDrive "extractor artifacts"
        $env:API_MANAGEMENT_SERVICE_OUTPUT_FOLDER_PATH = $extractorArtifactsPath        
        $output = & $extractorFilePath
        if ($LASTEXITCODE -ne 0) {
            throw "Extractor failed. Output is $($output | Out-String)"
        }
    }

    Context "Policy fragments" -ForEach $parameters {
        BeforeDiscovery {
            $policyFragmentsFolder = Join-Path $testArtifactsPath "policy fragments"
            $policyFragments = $policyFragmentsFolder | Get-ChildItem -Directory
            $parameters = $policyFragments | ForEach-Object {
                @{
                    name          = $_.Name
                    artifactsPath = $_.FullName
                }
            }
        }

        It "Publishes XML successfully (<_.name>)" -ForEach $parameters {
            $testXmlPath = Join-Path $artifactsPath "policy.xml"
            $testXmlContent = Get-Content -Path $testXmlPath -Raw

            $extractorXmlPath = Join-Path $extractorArtifactsPath "policy.xml"
            $extractorXmlContent = Get-Content -Path $extractorXmlPath -Raw

            $extractorXmlContent | Should -Be $testXmlContent
        }
    }
}