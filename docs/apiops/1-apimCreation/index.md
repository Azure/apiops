---
title: Azure Apim Creation
has_children: false
nav_order: 2
---


## Create an API Management instance

An instance can take some time to provision. Expect ~45-75 minutes. Therefore, please create an instance prior to any demo.

[![Deploy to Azure](https://aka.ms/deploytoazurebutton)](https://portal.azure.com/#create/Microsoft.ApiManagement)

Using either your own or [Azure's common naming convention](https://docs.microsoft.com/en-us/azure/cloud-adoption-framework/ready/azure-best-practices/resource-naming), fill in each required field and press *Review + Create*, followed by *Create* to provision the service. Once started, it is not necessary to remain on this page or in the Azure Portal. If you entered a valid email address, you will receive a provisioning completion email.

Please note that the **service name must be unique**. We recommend to include your initials and numeric date.

> Take note of APIM service name as you will need it for forming URLs in this lab.

Please use the **Developer** tier, which provides [all relevant features at the lowest cost](https://azure.microsoft.com/en-us/pricing/details/api-management/#pricing). 

![APIM deploy blade](../../assets/images/apim-deploy-blade.png)
