<#

.SYNOPSIS
This script creates an API Ops extractor configuration based on the provided parameters.

.DESCRIPTION
It gathers information from the specified Azure API Management instance and generates an API Ops extractor configuration file.
It requires at least one of SubscriptionId or SubscriptionName, along with ApimInstanceName, ResourceGroupName, and Stage.

.PARAMETER Environment
The environment to use for the extractor configuration file. The default value is "AzureUSGovernment". Valid values are "AzureUSGovernment" and "AzureCloud"

.PARAMETER TenantId
The tenant ID to use for the extractor configuration file. This is the ID of the tenant in the Azure portal. Required because otherwise the latest version of Azure Powershell starts trying to connect to all the tenants available to the user then bugs out.

.PARAMETER SubscriptionId
The subscription ID to use for the extractor configuration file. This is the ID of the subscription in the Azure portal. This parameter is required if the SubscriptionName parameter is not specified.

.PARAMETER ApimInstanceName
The name of the APIM instance to use for the extractor configuration file. This is the name of the APIM instance in the Azure portal.

.PARAMETER ResourceGroupName
The name of the resource group that contains the APIM instance to use for the extractor configuration file. This is the name of the resource group in the Azure portal.

.PARAMETER Stage
The name of the stage to use for the extractor configuration file. This is the name of the stage in the Azure portal.

.EXAMPLE
. .\New-ExtractorConfiguration.ps1 -TenantId 'tenant-id' -ApimInstanceName 'apim-instance' -ResourceGroupName 'rg-of-apiminstance' -Stage 'dev' -SubscriptionId 'subscriptionid-of-apim'

.NOTES
Ensure you have the necessary Azure permissions to execute the script.

#>

param(
    [string]
    [ValidateSet("AzureUSGovernment","AzureCloud")]
    $Environment = "AzureCloud",

    [string]
    [Parameter(Mandatory)]
    $TenantId = '',

    [string]
    [Parameter(Mandatory)]
    $SubscriptionId = '',

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
    Connect-AzAccount -Tenant $TenantId -Environment $Environment -Subscription $SubscriptionId
}

# Encapsulate potential error throwing operations within a try-catch block
try {
    $apim = New-AzApiManagementContext -ResourceGroupName $ResourceGroupName -ServiceName $ApimInstanceName
    $apis = Get-AzApiManagementApi -Context $apim
    $loggers = Get-AzApiManagementLogger -Context $apim
    $namedValues = Get-AzApiManagementNamedValue -Context $apim
    $products = Get-AzApiManagementProduct -Context $apim
    $backends = Get-AzApiManagementBackend -Context $apim

    $uri = switch ($Environment) {
        "AzureUsGovernment" { "https://management.usgovcloudapi.net" }
        "AzureCloud" { "https://management.azure.com" }
    }

    $token = (ConvertFrom-SecureString (Get-AzAccessToken -ResourceUrl $uri).Token -AsPlainText)
    $headers = @{
        "Authorization" = "Bearer $token"
    }
    $TagsUri = "$uri/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroupName/providers/Microsoft.ApiManagement/service/$ApimInstanceName/tags?api-version=2022-08-01"
    $TagsUri
    $tags = (Invoke-RestMethod -Method GET -Uri $TagsUri -Headers $headers).value.name

} catch {
    Write-Error "$_"
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
Write-Output "Done listing APIs, tags, loggers, namedValues, products, and backends"
