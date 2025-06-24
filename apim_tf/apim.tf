#
# Terraform Script to Create a Simplified Azure APIM Test Environment
#
# This script provisions:
# 1. Two new resource groups to hold each APIM instance.
# 2. Two Azure API Management (APIM) instances on the cost-effective "Consumption" tier.
#

# 1. Configure the Azure Provider
# -----------------------------------------------------------------------------
# Specifies the required providers for this configuration. We need the Azure
# provider to create resources in Azure and the Random provider to generate
# unique names for our resources to prevent naming conflicts.
# -----------------------------------------------------------------------------
terraform {
  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~>3.0"
    }
    random = {
      source  = "hashicorp/random"
      version = "~>3.1"
    }
  }
}

# Authenticates with Azure. Terraform will automatically use your logged-in
# Azure CLI session.
provider "azurerm" {
  features {}
}

# 2. Generate a Random Suffix for Resource Naming
# -----------------------------------------------------------------------------
# This resource creates a random string that will be appended to resource
# names to ensure they are globally unique.
# -----------------------------------------------------------------------------
resource "random_string" "unique" {
  length  = 6
  special = false
  upper   = false
}

# 3. Create Resource Groups
# -----------------------------------------------------------------------------
# We create two separate resource groups:
# - apim_rg_1: To hold the first APIM instance.
# - apim_rg_2: To hold the second APIM instance.
# -----------------------------------------------------------------------------
resource "azurerm_resource_group" "apim_rg_1" {
  name     = "rg-apim-test-1-${random_string.unique.result}"
  location = "East US"
}

resource "azurerm_resource_group" "apim_rg_2" {
  name     = "rg-apim-test-2-${random_string.unique.result}"
  location = "East US"
}

# 4. Create API Management Instances
# -----------------------------------------------------------------------------
# This section provisions the two APIM instances.
# - sku_name is set to "Consumption_0", which is the serverless, pay-per-use
#   tier, making it the most economical choice for dev/test workloads.
# -----------------------------------------------------------------------------
resource "azurerm_api_management" "apim_instance_1" {
  name                = "apim-test-1-${random_string.unique.result}"
  location            = azurerm_resource_group.apim_rg_1.location
  resource_group_name = azurerm_resource_group.apim_rg_1.name
  publisher_name      = "Test Publisher 1"
  publisher_email     = "contact-1@example.com"
  sku_name            = "Consumption_0"
}

resource "azurerm_api_management" "apim_instance_2" {
  name                = "apim-test-2-${random_string.unique.result}"
  location            = azurerm_resource_group.apim_rg_2.location
  resource_group_name = azurerm_resource_group.apim_rg_2.name
  publisher_name      = "Test Publisher 2"
  publisher_email     = "contact-2@example.com"
  sku_name            = "Consumption_0"
}

# 5. Define Outputs
# -----------------------------------------------------------------------------
# Outputs make it easy to retrieve the names and resource groups of the
# created resources after Terraform applies the configuration.
# -----------------------------------------------------------------------------
output "apim_instance_1_name" {
  description = "The name of the first APIM instance."
  value       = azurerm_api_management.apim_instance_1.name
}

output "apim_instance_1_resource_group" {
  description = "The resource group of the first APIM instance."
  value       = azurerm_resource_group.apim_rg_1.name
}

output "apim_instance_2_name" {
  description = "The name of the second APIM instance."
  value       = azurerm_api_management.apim_instance_2.name
}

output "apim_instance_2_resource_group" {
  description = "The resource group of the second APIM instance."
  value       = azurerm_resource_group.apim_rg_2.name
}
