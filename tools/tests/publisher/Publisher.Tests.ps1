param (
    [Parameter(Mandatory)]
    [string]
    $ResourceGroupName,
    [Parameter(Mandatory)]
    [string]
    $ApiManagementInstanceName,
    [Parameter(Mandatory)]
    [string]
    $ExtractorExePath,
    [Parameter(Mandatory)]
    [string]
    $PublisherExePath,
    [Parameter(Mandatory)]
    [string]
    $TestArtifactsPath
)

Describe "Publisher tests" {
    BeforeAll {
        Set-StrictMode -Version Latest
        $ErrorActionPreference = "Stop"
        $VerbosePreference = "Continue"
        $InformationPreference = "Continue"
        
        Write-Information "Running publisher..."
        $env:AZURE_BEARER_TOKEN = (Get-AzAccessToken).Token
        $env:AZURE_SUBSCRIPTION_ID = (Get-AzContext).Subscription.Id
        $env:AZURE_RESOURCE_GROUP_NAME = $ResourceGroupName
        $env:API_MANAGEMENT_SERVICE_NAME = $ApiManagementInstanceName           
        $env:API_MANAGEMENT_SERVICE_OUTPUT_FOLDER_PATH = $TestArtifactsPath        
        $output = & $PublisherExePath
        $output
        if ($LASTEXITCODE -ne 0) {
            throw "Publisher failed."
        }
            
        Write-Information "Running extractor..."
        $extractorArtifactsPath = Join-Path $TestDrive "extractor artifacts"
        $env:API_MANAGEMENT_SERVICE_OUTPUT_FOLDER_PATH = $extractorArtifactsPath        
        $output = & $ExtractorExePath
        $output
        if ($LASTEXITCODE -ne 0) {
            throw "Extractor failed."
        }
    }

    Context "Policy fragments" {
        BeforeDiscovery {
            $policyFragmentsFolder = Join-Path $TestArtifactsPath "policy fragments"
            Write-Information "Path is $TestArtifactsPath"
            Get-ChildItem "/home/runner/work" -Recurse | ForEach-Object { Write-Information "Child path is $($_.FullName)" }
            $policyFragments = "$policyFragmentsFolder" | Get-ChildItem -Directory
            $parameters = $policyFragments | ForEach-Object {
                @{
                    name                = $_.Name
                    artifactsFolderPath = $_.FullName
                }
            }
        }

        It "Publishes XML successfully (<_.name>)" -ForEach $parameters {
            $testXmlPath = Join-Path $artifactsFolderPath "policy.xml"
            $testXmlContent = Get-Content -Path $testXmlPath -Raw

            $extractorXmlPath = Join-Path $extractorArtifactsPath "policy fragments" "$name" "policy.xml"
            $extractorXmlContent = Get-Content -Path $extractorXmlPath -Raw

            $extractorXmlContent | Should -Be $testXmlContent
        }
    }
}