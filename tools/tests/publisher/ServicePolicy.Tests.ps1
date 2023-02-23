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
    $parameters = Join-Path "$PublisherArtifactsPath" "*" |
                    Get-ChildItem -File -Include "*.xml" |
                    ForEach-Object {
                        @{
                            FileName = $_.Name
                        }
                    }
}

Describe "service policy <_.FileName>" -ForEach $parameters {
    BeforeDiscovery {
    }

    It "Publishes XML successfully" {
        $publisherXmlPath = Join-Path "$PublisherArtifactsPath" "$FileName"
        $publisherXmlContent = Get-Content -Path "$publisherXmlPath" -Raw

        $extractorXmlPath = Join-Path "$ExtractorArtifactsPath" "$FileName"
        $extractorXmlContent = Get-Content -Path "$extractorXmlPath" -Raw

        $extractorXmlContent | Should -Be $publisherXmlContent
    }
}