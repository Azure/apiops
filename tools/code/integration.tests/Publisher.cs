using common;
using common.tests;
using CsCheck;
using LanguageExt;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
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

public delegate ValueTask RunPublisher(PublisherOptions options, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, Option<CommitId> commitIdOption, CancellationToken cancellationToken);
public delegate ValueTask ValidatePublishedArtifacts(PublisherOptions options, Option<CommitId> commitIdOption, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

public sealed record PublisherOptions
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
        from namedValues in GenerateOverrides(serviceModel.NamedValues, NamedValueModule.GetDtoDictionary, NamedValueModule.GenerateOverride)
        from tags in GenerateOverrides(serviceModel.Tags, TagModule.GetDtoDictionary, TagModule.GenerateOverride)
        from gateways in GenerateOverrides(serviceModel.Gateways, GatewayModule.GetDtoDictionary, GatewayModule.GenerateOverride)
        from versionSets in GenerateOverrides(serviceModel.VersionSets, VersionSetModule.GetDtoDictionary, VersionSetModule.GenerateOverride)
        from backends in GenerateOverrides(serviceModel.Backends, BackendModule.GetDtoDictionary, BackendModule.GenerateOverride)
        from loggers in GenerateOverrides(serviceModel.Loggers, LoggerModule.GetDtoDictionary, LoggerModule.GenerateOverride)
        from diagnostics in GenerateOverrides(serviceModel.Diagnostics, DiagnosticModule.GetDtoDictionary, DiagnosticModule.GenerateOverride)
        from policyFragments in GenerateOverrides(serviceModel.PolicyFragments, PolicyFragmentModule.GetDtoDictionary, PolicyFragmentModule.GenerateOverride)
        from servicePolicies in GenerateOverrides(serviceModel.ServicePolicies, ServicePolicyModule.GetDtoDictionary, ServicePolicyModule.GenerateOverride)
        from products in GenerateOverrides(serviceModel.Products, ProductModule.GetDtoDictionary, ProductModule.GenerateOverride)
        from groups in GenerateOverrides(serviceModel.Groups, GroupModule.GetDtoDictionary, GroupModule.GenerateOverride)
        from apis in GenerateOverrides(serviceModel.Apis, ApiModule.GetDtoDictionary, ApiModule.GenerateOverride)
        from subscriptions in GenerateOverrides(serviceModel.Subscriptions, SubscriptionModule.GetDtoDictionary, SubscriptionModule.GenerateOverride)
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
        from overrides in modelsToOverride.Select(model => from newDto in dtoGen(model.Value)
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
        var overridesJson = overrides.Select(kvp => JsonObjectExtensions.Parse(kvp.Value)
                                                                        .SetProperty("name", getNameToWrite(kvp.Key)))
                                     .ToJsonArray();

        return jsonObject.SetProperty(sectionName, overridesJson);
    }

    public static FrozenDictionary<TName, TDto> Override<TName, TDto>(IDictionary<TName, TDto> dtos, IDictionary<TName, TDto> overrides) where TName : notnull =>
        dtos.Select(kvp =>
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

public static class PublisherModule
{
    public static void ConfigureRunPublisher(IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(GetRunPublisher);
    }

    private static RunPublisher GetRunPublisher(IServiceProvider provider)
    {
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (options, serviceName, serviceDirectory, commitIdOption, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(RunPublisher))
                                       ?.AddTag("commit.id", commitIdOption.ToString());

            logger.LogInformation("Running publisher...");

            var configurationFileOption = await tryGetConfigurationYamlFile(options, serviceDirectory, cancellationToken);
            var arguments = getArguments(serviceName, serviceDirectory, configurationFileOption, commitIdOption, cancellationToken);
            await publisher.Program.Main(arguments);
        };

        static async ValueTask<Option<FileInfo>> tryGetConfigurationYamlFile(PublisherOptions options, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
        {
            var optionsJson = options.ToJsonObject();
            if (optionsJson.Count == 0)
            {
                return Option<FileInfo>.None;
            }

            var yamlFilePath = Path.Combine(serviceDirectory.ToDirectoryInfo().FullName, "configuration.publisher.yaml");
            var yamlFile = new FileInfo(yamlFilePath);
            await writeYamlToFile(optionsJson, yamlFile, cancellationToken);

            return yamlFile;
        }

        static async ValueTask writeYamlToFile(JsonNode json, FileInfo file, CancellationToken cancellationToken)
        {
            var yaml = YamlConverter.Serialize(json);
            var content = BinaryData.FromString(yaml);
            await file.OverwriteWithBinaryData(content, cancellationToken);
        }

        static string[] getArguments(ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, Option<FileInfo> configurationFileOption, Option<CommitId> commitIdOption, CancellationToken cancellationToken)
        {
            var argumentDictionary = new Dictionary<string, string>
            {
                [$"{getApiManagementServiceNameParameter()}"] = serviceName.ToString(),
                ["API_MANAGEMENT_SERVICE_OUTPUT_FOLDER_PATH"] = serviceDirectory.ToDirectoryInfo().FullName
            };

            configurationFileOption.Iter(file => argumentDictionary.Add("CONFIGURATION_YAML_PATH", file.FullName));

            commitIdOption.Iter(id => argumentDictionary.Add("COMMIT_ID", id.Value));

            return argumentDictionary.Aggregate(Array.Empty<string>(), (arguments, kvp) => [.. arguments, $"--{kvp.Key}", kvp.Value]);
        }

        static string getApiManagementServiceNameParameter() =>
            Gen.OneOfConst("API_MANAGEMENT_SERVICE_NAME", "apimServiceName").Single();
    }

    public static void ConfigureValidatePublishedArtifacts(IHostApplicationBuilder builder)
    {
        NamedValueModule.ConfigureValidatePublishedNamedValues(builder);
        TagModule.ConfigureValidatePublishedTags(builder);
        VersionSetModule.ConfigureValidatePublishedVersionSets(builder);
        BackendModule.ConfigureValidatePublishedBackends(builder);
        LoggerModule.ConfigureValidatePublishedLoggers(builder);
        DiagnosticModule.ConfigureValidatePublishedDiagnostics(builder);
        PolicyFragmentModule.ConfigureValidatePublishedPolicyFragments(builder);
        ServicePolicyModule.ConfigureValidatePublishedServicePolicies(builder);
        GroupModule.ConfigureValidatePublishedGroups(builder);
        ProductModule.ConfigureValidatePublishedProducts(builder);
        ApiModule.ConfigureValidatePublishedApis(builder);
        SubscriptionModule.ConfigureValidatePublishedSubscriptions(builder);

        builder.Services.TryAddSingleton(GetValidatePublishedArtifacts);
    }

    private static ValidatePublishedArtifacts GetValidatePublishedArtifacts(IServiceProvider provider)
    {
        var validateNamedValues = provider.GetRequiredService<ValidatePublishedNamedValues>();
        var validateTags = provider.GetRequiredService<ValidatePublishedTags>();
        var validateVersionSets = provider.GetRequiredService<ValidatePublishedVersionSets>();
        var validateBackends = provider.GetRequiredService<ValidatePublishedBackends>();
        var validateLoggers = provider.GetRequiredService<ValidatePublishedLoggers>();
        var validateDiagnostics = provider.GetRequiredService<ValidatePublishedDiagnostics>();
        var validatePolicyFragments = provider.GetRequiredService<ValidatePublishedPolicyFragments>();
        var validateServicePolicies = provider.GetRequiredService<ValidatePublishedServicePolicies>();
        var validateGroups = provider.GetRequiredService<ValidatePublishedGroups>();
        var validateProducts = provider.GetRequiredService<ValidatePublishedProducts>();
        var validateApis = provider.GetRequiredService<ValidatePublishedApis>();
        var validateSubscriptions = provider.GetRequiredService<ValidatePublishedSubscriptions>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (options, commitIdOption, serviceName, serviceDirectory, cancellationToken) =>
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
            await validateSubscriptions(options.SubscriptionOverrides, commitIdOption, serviceName, serviceDirectory, cancellationToken);
        };
    }
}