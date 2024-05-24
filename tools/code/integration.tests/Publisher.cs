using common;
using common.tests;
using CsCheck;
using LanguageExt;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using publisher;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using YamlDotNet.System.Text.Json;

namespace integration.tests;

internal delegate ValueTask RunPublisher(PublisherOptions options, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, Option<CommitId> commitIdOption, CancellationToken cancellationToken);

internal delegate ValueTask ValidatePublishedArtifacts(PublisherOptions options, Option<CommitId> commitIdOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

internal sealed record PublisherOptions
{
    public required FrozenDictionary<NamedValueName, NamedValueDto> NamedValueOverrides { get; init; }
    public required FrozenDictionary<TagName, TagDto> TagOverrides { get; init; }
    public required FrozenDictionary<GatewayName, GatewayDto> GatewayOverrides { get; init; }
    public required FrozenDictionary<VersionSetName, VersionSetDto> VersionSetOverrides { get; init; }
    public required FrozenDictionary<BackendName, BackendDto> BackendOverrides { get; init; }
    public required FrozenDictionary<LoggerName, LoggerDto> LoggerOverrides { get; init; }
    public required FrozenDictionary<DiagnosticName, DiagnosticDto> DiagnosticOverrides { get; init; }
    public required FrozenDictionary<PolicyFragmentName, PolicyFragmentDto> PolicyFragmentOverrides { get; init; }
    public required FrozenDictionary<ServicePolicyName, ServicePolicyDto> ServicePolicyOverrides { get; init; }
    public required FrozenDictionary<ProductName, ProductDto> ProductOverrides { get; init; }
    public required FrozenDictionary<GroupName, GroupDto> GroupOverrides { get; init; }
    public required FrozenDictionary<ApiName, ApiDto> ApiOverrides { get; init; }
    public required FrozenDictionary<SubscriptionName, SubscriptionDto> SubscriptionOverrides { get; init; }

    public static Gen<PublisherOptions> Generate(ServiceModel serviceModel) =>
        from namedValues in GenerateOverrides(serviceModel.NamedValues, NamedValue.GetDtoDictionary, NamedValue.GenerateOverride)
        from tags in GenerateOverrides(serviceModel.Tags, Tag.GetDtoDictionary, Tag.GenerateOverride)
        from gateways in GenerateOverrides(serviceModel.Gateways, Gateway.GetDtoDictionary, Gateway.GenerateOverride)
        from versionSets in GenerateOverrides(serviceModel.VersionSets, VersionSet.GetDtoDictionary, VersionSet.GenerateOverride)
        from backends in GenerateOverrides(serviceModel.Backends, Backend.GetDtoDictionary, Backend.GenerateOverride)
        from loggers in GenerateOverrides(serviceModel.Loggers, Logger.GetDtoDictionary, Logger.GenerateOverride)
        from diagnostics in GenerateOverrides(serviceModel.Diagnostics, Diagnostic.GetDtoDictionary, Diagnostic.GenerateOverride)
        from policyFragments in GenerateOverrides(serviceModel.PolicyFragments, PolicyFragment.GetDtoDictionary, PolicyFragment.GenerateOverride)
        from servicePolicies in GenerateOverrides(serviceModel.ServicePolicies, ServicePolicy.GetDtoDictionary, ServicePolicy.GenerateOverride)
        from products in GenerateOverrides(serviceModel.Products, Product.GetDtoDictionary, Product.GenerateOverride)
        from groups in GenerateOverrides(serviceModel.Groups, Group.GetDtoDictionary, Group.GenerateOverride)
        from apis in GenerateOverrides(serviceModel.Apis, Api.GetDtoDictionary, Api.GenerateOverride)
        from subscriptions in GenerateOverrides(serviceModel.Subscriptions, Subscription.GetDtoDictionary, Subscription.GenerateOverride)
        select new PublisherOptions
        {
            NamedValueOverrides = namedValues,
            TagOverrides = tags,
            GatewayOverrides = gateways,
            VersionSetOverrides = versionSets,
            BackendOverrides = backends,
            LoggerOverrides = loggers,
            DiagnosticOverrides = diagnostics,
            PolicyFragmentOverrides = policyFragments,
            ServicePolicyOverrides = servicePolicies,
            ProductOverrides = products,
            GroupOverrides = groups,
            ApiOverrides = apis,
            SubscriptionOverrides = subscriptions
        };

    private static Gen<FrozenDictionary<TName, TDto>> GenerateOverrides<TModel, TName, TDto>(IEnumerable<TModel> models, Func<IEnumerable<TModel>, FrozenDictionary<TName, TDto>> getDictionary, Func<TDto, Gen<TDto>> dtoGen) where TName : notnull
    {
        var modelDictionary = getDictionary(models);
        return GenerateOverrides(modelDictionary, dtoGen);
    }

    private static Gen<FrozenDictionary<TName, TDto>> GenerateOverrides<TName, TDto>(IDictionary<TName, TDto> models, Func<TDto, Gen<TDto>> dtoGen) where TName : notnull =>
        from modelsToOverride in Generator.SubImmutableArrayOf(models)
        from overrides in modelsToOverride.Map(model => from newDto in dtoGen(model.Value)
                                                        select (model.Key, newDto))
                                          .SequenceToFrozenSet(x => x.Key)
        select overrides.ToFrozenDictionary();

    public JsonObject ToJsonObject()
    {
        var json = new JsonObject();

        json = AddDtoOverrides(json);

        return json;
    }

    private JsonObject AddDtoOverrides(JsonObject jsonObject)
    {
        JsonObject addDtoOverride(JsonObject jsonObject, string propertyName)
        {
            var propertyExpression = Expression.Property(Expression.Constant(this), propertyName);
            var types = propertyExpression.Type.GetGenericArguments();
            var body = Expression.Call(typeof(PublisherOptions), nameof(AddDtoOverrides), types, propertyExpression, Expression.Constant(jsonObject));
            var lambda = Expression.Lambda<Func<JsonObject>>(body);
            return lambda.Compile()();
        }

        return typeof(PublisherOptions)
                .GetProperties()
                .Where(property => property.PropertyType.IsGenericType
                                   && property.PropertyType.GetGenericTypeDefinition() == typeof(FrozenDictionary<,>)
                                   && property.Name.EndsWith("Overrides", StringComparison.OrdinalIgnoreCase))
                .Select(property => property.Name)
                .Aggregate(jsonObject, addDtoOverride);
    }

    private static JsonObject AddDtoOverrides<TName, TDto>(FrozenDictionary<TName, TDto> overrides, JsonObject jsonObject) where TName : notnull
    {
        var sectionName = OverrideDtoFactory.GetSectionName<TName>();
        var getNameToWrite = (TName name) => (JsonNode?)OverrideDtoFactory.GetNameToFind(name);
        var overridesJson = overrides.Map(kvp => JsonObjectExtensions.Parse(kvp.Value)
                                                                     .SetProperty("name", getNameToWrite(kvp.Key)))
                                     .ToJsonArray();

        return jsonObject.SetProperty(sectionName, overridesJson);
    }

    public static FrozenDictionary<TName, TDto> Override<TName, TDto>(IDictionary<TName, TDto> dtos, IDictionary<TName, TDto> overrides) where TName : notnull =>
        dtos.Map(kvp =>
        {
            var key = kvp.Key switch
            {
                ApiName apiName => (TName)(object)ApiName.GetRootName(apiName),
                var name => name
            };

            var value = overrides.Find(key)
                                 .Map(overrideDto => OverrideDtoFactory.Override(kvp.Value, JsonObjectExtensions.Parse(overrideDto)))
                                 .IfNone(kvp.Value);

            return (kvp.Key, value);
        }).ToFrozenDictionary();
}

file sealed class RunPublisherHandler(ILogger<RunPublisher> logger,
                                      ActivitySource activitySource,
                                      GetSubscriptionId getSubscriptionId,
                                      GetResourceGroupName getResourceGroupName,
                                      GetBearerToken getBearerToken)
{
    public async ValueTask Handle(PublisherOptions options, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, Option<CommitId> commitIdOption, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(RunPublisher));

        logger.LogInformation("Running publisher...");

        var configurationFileOption = await TryGetConfigurationYamlFile(options, serviceDirectory, cancellationToken);
        var arguments = await GetArguments(serviceName, serviceDirectory, configurationFileOption, commitIdOption, cancellationToken);
        await publisher.Program.Main(arguments);
    }

    private static async ValueTask<Option<FileInfo>> TryGetConfigurationYamlFile(PublisherOptions options, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        var optionsJson = options.ToJsonObject();
        if (optionsJson.Count == 0)
        {
            return Option<FileInfo>.None;
        }

        var yamlFilePath = Path.Combine(serviceDirectory.ToDirectoryInfo().FullName, "configuration.publisher.yaml");
        var yamlFile = new FileInfo(yamlFilePath);
        await WriteYamlToFile(optionsJson, yamlFile, cancellationToken);

        return yamlFile;
    }

    private static async ValueTask WriteYamlToFile(JsonNode json, FileInfo file, CancellationToken cancellationToken)
    {
        var yaml = YamlConverter.Serialize(json);
        var content = BinaryData.FromString(yaml);
        await file.OverwriteWithBinaryData(content, cancellationToken);
    }

    private async ValueTask<string[]> GetArguments(ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, Option<FileInfo> configurationFileOption, Option<CommitId> commitIdOption, CancellationToken cancellationToken)
    {
        var argumentDictionary = new Dictionary<string, string>
        {
            [$"{GetApiManagementServiceNameParameter()}"] = serviceName.ToString(),
            ["API_MANAGEMENT_SERVICE_OUTPUT_FOLDER_PATH"] = serviceDirectory.ToDirectoryInfo().FullName,
            ["AZURE_SUBSCRIPTION_ID"] = getSubscriptionId(),
            ["AZURE_RESOURCE_GROUP_NAME"] = getResourceGroupName(),
            ["AZURE_BEARER_TOKEN"] = await getBearerToken(cancellationToken)
        };

#pragma warning disable CA1849 // Call async methods when in an async method
        configurationFileOption.Iter(file => argumentDictionary.Add("CONFIGURATION_YAML_PATH", file.FullName));
        commitIdOption.Iter(id => argumentDictionary.Add("COMMIT_ID", id.Value));
#pragma warning restore CA1849 // Call async methods when in an async method

        return argumentDictionary.Aggregate(Array.Empty<string>(), (arguments, kvp) => [.. arguments, $"--{kvp.Key}", kvp.Value]);
    }

    private static string GetApiManagementServiceNameParameter() =>
        Gen.OneOfConst("API_MANAGEMENT_SERVICE_NAME", "apimServiceName").Single();
}

file sealed class ValidatePublishedArtifactsHandler(ILogger<ValidatePublishedArtifacts> logger,
                                                    ActivitySource activitySource,
                                                    ValidatePublishedNamedValues validateNamedValues,
                                                    ValidatePublishedTags validateTags,
                                                    ValidatePublishedVersionSets validateVersionSets,
                                                    ValidatePublishedBackends validateBackends,
                                                    ValidatePublishedLoggers validateLoggers,
                                                    ValidatePublishedDiagnostics validateDiagnostics,
                                                    ValidatePublishedPolicyFragments validatePolicyFragments,
                                                    ValidatePublishedServicePolicies validateServicePolicies,
                                                    ValidatePublishedGroups validateGroups,
                                                    ValidatePublishedProducts validateProducts,
                                                    ValidatePublishedApis validateApis)
{
    public async ValueTask Handle(PublisherOptions options, Option<CommitId> commitIdOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(ValidatePublishedArtifacts));

        logger.LogInformation("Validating published artifacts...");

        await validateNamedValues(options.NamedValueOverrides, commitIdOption, serviceName, serviceDirectory, cancellationToken);
        await validateTags(options.TagOverrides, commitIdOption, serviceName, serviceDirectory, cancellationToken);
        await validateVersionSets(options.VersionSetOverrides, commitIdOption, serviceName, serviceDirectory, cancellationToken);
        await validateBackends(options.BackendOverrides, commitIdOption, serviceName, serviceDirectory, cancellationToken);
        await validateLoggers(options.LoggerOverrides, commitIdOption, serviceName, serviceDirectory, cancellationToken);
        await validateDiagnostics(options.DiagnosticOverrides, commitIdOption, serviceName, serviceDirectory, cancellationToken);
        await validatePolicyFragments(options.PolicyFragmentOverrides, commitIdOption, serviceName, serviceDirectory, cancellationToken);
        await validateServicePolicies(options.ServicePolicyOverrides, commitIdOption, serviceName, serviceDirectory, cancellationToken);
        await validateGroups(options.GroupOverrides, commitIdOption, serviceName, serviceDirectory, cancellationToken);
        await validateProducts(options.ProductOverrides, commitIdOption, serviceName, serviceDirectory, cancellationToken);
        await validateApis(options.ApiOverrides, commitIdOption, serviceName, serviceDirectory, cancellationToken);
    }
}

internal static class PublisherServices
{
    public static void ConfigureRunPublisher(IServiceCollection services)
    {
        services.TryAddSingleton<RunPublisherHandler>();
        services.TryAddSingleton<RunPublisher>(provider => provider.GetRequiredService<RunPublisherHandler>().Handle);
    }

    public static void ConfigureValidatePublishedArtifacts(IServiceCollection services)
    {
        NamedValueServices.ConfigureValidatePublishedNamedValues(services);
        TagServices.ConfigureValidatePublishedTags(services);
        VersionSetServices.ConfigureValidatePublishedVersionSets(services);
        BackendServices.ConfigureValidatePublishedBackends(services);
        LoggerServices.ConfigureValidatePublishedLoggers(services);
        DiagnosticServices.ConfigureValidatePublishedDiagnostics(services);
        PolicyFragmentServices.ConfigureValidatePublishedPolicyFragments(services);
        ServicePolicyServices.ConfigureValidatePublishedServicePolicies(services);
        GroupServices.ConfigureValidatePublishedGroups(services);
        ProductServices.ConfigureValidatePublishedProducts(services);
        ApiServices.ConfigureValidatePublishedApis(services);

        services.TryAddSingleton<ValidatePublishedArtifactsHandler>();
        services.TryAddSingleton<ValidatePublishedArtifacts>(provider => provider.GetRequiredService<ValidatePublishedArtifactsHandler>().Handle);
    }
}

internal static class Publisher
{
    public static async ValueTask Run(PublisherOptions options, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, string subscriptionId, string resourceGroupName, string bearerToken, Option<CommitId> commitId, CancellationToken cancellationToken)
    {
        var argumentDictionary = new Dictionary<string, string>
        {
            [$"{GetApiManagementServiceNameParameter()}"] = serviceName.ToString(),
            ["API_MANAGEMENT_SERVICE_OUTPUT_FOLDER_PATH"] = serviceDirectory.ToDirectoryInfo().FullName,
            ["AZURE_SUBSCRIPTION_ID"] = subscriptionId,
            ["AZURE_RESOURCE_GROUP_NAME"] = resourceGroupName,
            ["AZURE_BEARER_TOKEN"] = bearerToken,
            ["Logging:LogLevel:Default"] = "Trace"
        };

#pragma warning disable CA1849 // Call async methods when in an async method
        commitId.Iter(id => argumentDictionary.Add("COMMIT_ID", id.Value));
#pragma warning restore CA1849 // Call async methods when in an async method

        var yamlFile = await WriteConfigurationYaml(options, serviceDirectory, cancellationToken);
        argumentDictionary.Add("CONFIGURATION_YAML_PATH", yamlFile.FullName);

        var arguments = argumentDictionary.Aggregate(Array.Empty<string>(), (arguments, kvp) => [.. arguments, $"--{kvp.Key}", kvp.Value]);
        await Program.Main(arguments);
    }

    private static string GetApiManagementServiceNameParameter() =>
        Gen.OneOfConst("API_MANAGEMENT_SERVICE_NAME", "apimServiceName").Single();

    private static async ValueTask<FileInfo> WriteConfigurationYaml(PublisherOptions options, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        var yamlFilePath = Path.Combine(serviceDirectory.ToDirectoryInfo().FullName, "configuration.publisher.yaml");
        var yamlFile = new FileInfo(yamlFilePath);
        var json = options.ToJsonObject();
        await WriteYamlToFile(json, yamlFile, cancellationToken);

        return yamlFile;
    }

    private static async ValueTask WriteYamlToFile(JsonNode json, FileInfo file, CancellationToken cancellationToken)
    {
        var yaml = YamlConverter.Serialize(json);
        var content = BinaryData.FromString(yaml);
        await file.OverwriteWithBinaryData(content, cancellationToken);
    }
}
