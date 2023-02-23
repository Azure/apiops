param (
    [Parameter(Mandatory)]
    [string]
    $PublisherArtifactsPath,
    [Parameter(Mandatory)]
    [string]
    $ExtractorArtifactsPath
)

BeforeAll {
    Set-StrictMode -Version Latest
    $ErrorActionPreference = "Stop"
    $VerbosePreference = "Continue"
    $InformationPreference = "Continue"
}

BeforeDiscovery {
    $policyFragmentsPathSection = "policy fragments"
    $parameters = Join-Path "$PublisherArtifactsPath" "$policyFragmentsPathSection" |
                    Get-ChildItem -Directory |
                    ForEach-Object {
                        @{
                            name                       = $_.Name
                            policyFragmentsPathSection = $policyFragmentsPathSection
                        }
                    }
}

Describe "policy fragment <_.name>" -ForEach $parameters {
    It "Publishes XML successfully" {
        $xmlPathSection = Join-Path "$policyFragmentsPathSection" "$name" "policy.xml"

        $publisherXmlPath = Join-Path "$PublisherArtifactsPath" "$xmlPathSection"
        $publisherXmlContent = Get-Content -Path "$publisherXmlPath" -Raw

        $extractorXmlPath = Join-Path "$ExtractorArtifactsPath" "$xmlPathSection"
        $extractorXmlContent = Get-Content -Path "$extractorXmlPath" -Raw

        $extractorXmlContent | Should -Be $publisherXmlContent
    }
}