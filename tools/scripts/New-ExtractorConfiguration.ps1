<#

.SYNOPSIS
This script creates an API Ops extractor configuration based on the provided parameters.

.DESCRIPTION
It gathers information from the specified Azure API Management instance and generates an API Ops extractor configuration file.
It requires at least one of SubscriptionId or SubscriptionName, along with ApimInstanceName, ResourceGroupName, and Stage.

.PARAMETER Environment
The environment to use for the extractor configuration file. The default value is "AzureUSGovernment". Valid values are "AzureUSGovernment" and "AzureCloud"

.PARAMETER SubscriptionId
The subscription ID to use for the extractor configuration file. This is the ID of the subscription in the Azure portal. This parameter is required if the SubscriptionName parameter is not specified.

.PARAMETER SubscriptionName
The subscription name to use for the extractor configuration file. This is the name of the subscription in the Azure portal. This parameter is required if the SubscriptionId parameter is not specified.

.PARAMETER ApimInstanceName
The name of the APIM instance to use for the extractor configuration file. This is the name of the APIM instance in the Azure portal.

.PARAMETER ResourceGroupName
The name of the resource group that contains the APIM instance to use for the extractor configuration file. This is the name of the resource group in the Azure portal.

.PARAMETER Stage
The name of the stage to use for the extractor configuration file. This is the name of the stage in the Azure portal.

.EXAMPLE
. .\New-ExtractorConfiguration.ps1 -ApimInstanceName 'apim-instance' -ResourceGroupName 'rg-of-apiminstance' -Stage 'dev' -SubscriptionName 'subscription-of-apim'

.NOTES
Ensure you have the necessary Azure permissions to execute the script.

#>

param(
    [string]
    [ValidateSet("AzureUSGovernment","AzureCloud")]
    $Environment = "AzureUsGovernment",

    [string]
    [Parameter(Mandatory, ParameterSetName = "SubscriptionId")]
    $SubscriptionId = '',

    [string]
    [Parameter(Mandatory, ParameterSetName = "SubscriptionName")]
    $SubscriptionName = '',

    [string]
    [Parameter(Mandatory)]
    $ApimInstanceName,
    
    [string]
    [Parameter(Mandatory)]
    $ResourceGroupName,

    [string]
    [Parameter(Mandatory)]
    $Stage = 'dev'
)

#region Azure Queries
$context = Get-AzContext
if ($null -eq $Context)
{
    Login-AzAccount -Environment $Environment
}
if ($SubscriptionId.Length -gt 0) {
    Set-AzContext -SubscriptionId $SubscriptionId
} 
else
{
    Set-AzContext -SubscriptionName $SubscriptionName
}

# Encapsulate potential error throwing operations within a try-catch block
try {
    $apim = New-AzApiManagementContext -ResourceGroupName $ResourceGroupName -ServiceName $ApimInstanceName
    $apis = Get-AzApiManagementApi -Context $apim
    $loggers = Get-AzApiManagementLogger -Context $apim
    $namedValues = Get-AzApiManagementNamedValue -Context $apim
    $products = Get-AzApiManagementProduct -Context $apim
    $backends = Get-AzApiManagementBackend -Context $apim

    $uri = $Environment -eq "AzureUsGovernment" ? "management.usgovcloudapi.net" : "management.azure.com"
    $headers = @{
        "Authorization" = "Bearer $((Get-AzAccessToken -ResourceUrl "https://$uri").Token)"
    }
    
    $tags = (Invoke-RestMethod -Method GET -Uri "https://$uri/subscriptions/$((Get-AzContext).Subscription.Id)/resourceGroups/$ResourceGroupName/providers/Microsoft.ApiManagement/service/$ApimInstanceName/tags?api-version=2022-08-01" -Headers $headers).value.name
    
} catch {
    Write-Error "Error: $_"
    exit 1
}
#endregion Azure Queries

#region Outputs
$StringBuilder = New-Object System.Text.StringBuilder
# Output the list of APIs
if ($apis.Count -gt 0) {
    [void]$StringBuilder.AppendLine("apiNames:")
    foreach ($api in $apis) {
        [void]$StringBuilder.AppendLine("  - $($api.ApiId)")
    }
}

# Output the list of API tags
if ($tags.Count -gt 0) {
    [void]$StringBuilder.AppendLine("`ntagNames:")
    foreach ($tag in $tags) {
        [void]$StringBuilder.AppendLine("  - $($tag)")
    }
}

# Output the list of loggers
if ($loggers.Count -gt 0) {
    [void]$StringBuilder.AppendLine("`nloggerNames:")
    foreach ($logger in $loggers) {
        [void]$StringBuilder.AppendLine("  - $($logger.LoggerId)")
    }
}

#Output the list of named values
if ($namedValues.Count -gt 0) {
    [void]$StringBuilder.AppendLine("`nnamedValueNames:")
    foreach ($namedValue in $namedValues) {
        [void]$StringBuilder.AppendLine("  - $($namedValue.NamedValueId)")
    }
}


#Output the list of products
if ($products.Count -gt 0) {
    [void]$StringBuilder.AppendLine("`nproductNames:")
    foreach ($product in $products) {
        [void]$StringBuilder.AppendLine("  - $($product.ProductId)")
    }
}

#Output the list of backends
if($backends.Count -gt 0){
    [void]$StringBuilder.AppendLine("`nbackendNames:")
    foreach ($backend in $backends) {
        [void]$StringBuilder.AppendLine("  - $($backend.backendId)")
    }
}
#endregion Outputs

# Write the generated configuration to a file
$fileContent = $StringBuilder.ToString()
Out-File -FilePath "autogen.configuration.extractor.$Stage.yaml" -InputObject $fileContent -Force

# Output completion message
Write-Output "Done listing APIs, tags, loggers, namedValues, products, and backends"
