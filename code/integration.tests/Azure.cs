//using Azure.Core;
//using Azure.ResourceManager;
//using Azure.ResourceManager.Resources;
//using common;
//using Microsoft.Extensions.DependencyInjection;
//using Microsoft.Extensions.DependencyInjection.Extensions;
//using Microsoft.Extensions.Hosting;
//using System;

//namespace integration.tests;

//internal static class AzureModule
//{
//    public static void ConfigureResourceGroupResource(IHostApplicationBuilder builder)
//    {
//        ConfigureArmClient(builder);
//        common.AzureModule.ConfigureSubscriptionId(builder);
//        common.AzureModule.ConfigureResourceGroupName(builder);

//        builder.Services.TryAddSingleton(GetResourceGroupResource);
//    }

//    private static ResourceGroupResource GetResourceGroupResource(IServiceProvider provider)
//    {
//        var armClient = provider.GetRequiredService<ArmClient>();
//        var subscriptionId = provider.GetRequiredService<SubscriptionId>();
//        var resourceGroupName = provider.GetRequiredService<ResourceGroupName>();

//        var resourceGroupId = ResourceGroupResource.CreateResourceIdentifier(subscriptionId.ToString(), resourceGroupName.ToString());

//        return armClient.GetResourceGroupResource(resourceGroupId);
//    }

//    public static void ConfigureArmClient(IHostApplicationBuilder builder)
//    {
//        common.AzureModule.ConfigureTokenCredential(builder);

//        builder.Services.TryAddSingleton(GetArmClient);
//    }

//    private static ArmClient GetArmClient(IServiceProvider provider)
//    {
//        var tokenCredential = provider.GetRequiredService<TokenCredential>();

//        return new ArmClient(tokenCredential);
//    }
//}
