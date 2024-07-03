using common;
using common.tests;
using CsCheck;
using extractor;
using LanguageExt;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
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

internal delegate ValueTask RunExtractor(ExtractorOptions options, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

internal delegate ValueTask ValidateExtractorArtifacts(ExtractorOptions options, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

internal sealed record ExtractorOptions
{
    public required Option<FrozenSet<NamedValueName>> NamedValueNamesToExport { get; init; }
    public required Option<FrozenSet<TagName>> TagNamesToExport { get; init; }
    public required Option<FrozenSet<GatewayName>> GatewayNamesToExport { get; init; }
    public required Option<FrozenSet<VersionSetName>> VersionSetNamesToExport { get; init; }
    public required Option<FrozenSet<BackendName>> BackendNamesToExport { get; init; }
    public required Option<FrozenSet<LoggerName>> LoggerNamesToExport { get; init; }
    public required Option<FrozenSet<DiagnosticName>> DiagnosticNamesToExport { get; init; }
    public required Option<FrozenSet<PolicyFragmentName>> PolicyFragmentNamesToExport { get; init; }
    public required Option<FrozenSet<ProductName>> ProductNamesToExport { get; init; }
    public required Option<FrozenSet<GroupName>> GroupNamesToExport { get; init; }
    public required Option<FrozenSet<ApiName>> ApiNamesToExport { get; init; }
    public required Option<ApiSpecification> DefaultApiSpecification { get; init; }
    public required Option<FrozenSet<SubscriptionName>> SubscriptionNamesToExport { get; init; }

    public static ExtractorOptions NoFilter { get; } = new()
    {
        ApiNamesToExport = Option<FrozenSet<ApiName>>.None,
        BackendNamesToExport = Option<FrozenSet<BackendName>>.None,
        DefaultApiSpecification = Option<ApiSpecification>.None,
        DiagnosticNamesToExport = Option<FrozenSet<DiagnosticName>>.None,
        GatewayNamesToExport = Option<FrozenSet<GatewayName>>.None,
        GroupNamesToExport = Option<FrozenSet<GroupName>>.None,
        LoggerNamesToExport = Option<FrozenSet<LoggerName>>.None,
        NamedValueNamesToExport = Option<FrozenSet<NamedValueName>>.None,
        PolicyFragmentNamesToExport = Option<FrozenSet<PolicyFragmentName>>.None,
        ProductNamesToExport = Option<FrozenSet<ProductName>>.None,
        SubscriptionNamesToExport = Option<FrozenSet<SubscriptionName>>.None,
        TagNamesToExport = Option<FrozenSet<TagName>>.None,
        VersionSetNamesToExport = Option<FrozenSet<VersionSetName>>.None
    };

    public static Gen<ExtractorOptions> Generate(ServiceModel service) =>
        from namedValues in GenerateOptionalNamesToExport<NamedValueName, NamedValueModel>(service.NamedValues)
        from tags in GenerateOptionalNamesToExport<TagName, TagModel>(service.Tags)
        from gateways in GenerateOptionalNamesToExport<GatewayName, GatewayModel>(service.Gateways)
        from versionSets in GenerateOptionalNamesToExport<VersionSetName, VersionSetModel>(service.VersionSets)
        from backends in GenerateOptionalNamesToExport<BackendName, BackendModel>(service.Backends)
        from loggers in GenerateOptionalNamesToExport<LoggerName, LoggerModel>(service.Loggers)
        from diagnostics in GenerateOptionalNamesToExport<DiagnosticName, DiagnosticModel>(service.Diagnostics)
        from policyFragments in GenerateOptionalNamesToExport<PolicyFragmentName, PolicyFragmentModel>(service.PolicyFragments)
        from products in GenerateOptionalNamesToExport<ProductName, ProductModel>(service.Products)
        from groups in GenerateOptionalNamesToExport<GroupName, GroupModel>(service.Groups)
        from apis in GenerateOptionalNamesToExport<ApiName, ApiModel>(service.Apis)
        from defaultApiSpecification in GenerateDefaultApiSpecificationOption()
        from subscriptions in GenerateOptionalNamesToExport<SubscriptionName, SubscriptionModel>(service.Subscriptions)
        select new ExtractorOptions
        {
            NamedValueNamesToExport = namedValues,
            TagNamesToExport = tags,
            GatewayNamesToExport = gateways,
            VersionSetNamesToExport = versionSets,
            BackendNamesToExport = backends,
            LoggerNamesToExport = loggers,
            DiagnosticNamesToExport = diagnostics,
            PolicyFragmentNamesToExport = policyFragments,
            ProductNamesToExport = products,
            GroupNamesToExport = groups,
            ApiNamesToExport = apis,
            DefaultApiSpecification = defaultApiSpecification,
            SubscriptionNamesToExport = subscriptions
        };

    private static Gen<Option<FrozenSet<TName>>> GenerateOptionalNamesToExport<TName, TModel>(IEnumerable<TModel> models) =>
        GenerateNamesToExport<TName, TModel>(models).OptionOf();

    private static Gen<FrozenSet<TName>> GenerateNamesToExport<TName, TModel>(IEnumerable<TModel> models)
    {
        // Generate the function var modelToName = (TModel model) => model.Name
        var parameterExpression = Expression.Parameter(typeof(TModel), "model");
        var propertyExpression = Expression.Property(parameterExpression, "Name");
        var lambdaExpression = Expression.Lambda<Func<TModel, TName>>(propertyExpression, parameterExpression);
        var modelToName = lambdaExpression.Compile();

        return Generator.SubFrozenSetOf(models.Select(modelToName));
    }

    private static Gen<Option<ApiSpecification>> GenerateDefaultApiSpecificationOption() =>
        Gen.OneOfConst(new ApiSpecification.Wadl() as ApiSpecification,
                       new ApiSpecification.OpenApi { Format = new OpenApiFormat.Json(), Version = new OpenApiVersion.V2() },
                       new ApiSpecification.OpenApi { Format = new OpenApiFormat.Yaml(), Version = new OpenApiVersion.V2() },
                       new ApiSpecification.OpenApi { Format = new OpenApiFormat.Json(), Version = new OpenApiVersion.V3() },
                       new ApiSpecification.OpenApi { Format = new OpenApiFormat.Yaml(), Version = new OpenApiVersion.V3() })
           .OptionOf();

    public JsonObject ToJsonObject()
    {
        var json = new JsonObject();
        json = AddNamesToExport(json);
        json = WriteDefaultApiSpecificationFormat(json);
        return json;
    }

    /// <summary>
    /// For each property of type Option<FrozenSet<T>> where the property name ends with "NamesToExport",
    /// add names to export to the json object.
    /// </summary>
    private JsonObject AddNamesToExport(JsonObject jsonObject)
    {
        JsonObject addNameToExport(JsonObject jsonObject, string propertyName)
        {
            var propertyExpression = Expression.Property(Expression.Constant(this), propertyName);
            var typeOfT = propertyExpression.Type.GetGenericArguments()[0].GetGenericArguments()[0];
            var body = Expression.Call(typeof(ExtractorOptions), nameof(AddNamesToExport), [typeOfT], propertyExpression, Expression.Constant(jsonObject));
            var lambda = Expression.Lambda<Func<JsonObject>>(body);
            return lambda.Compile()();
        }


        return typeof(ExtractorOptions)
                .GetProperties()
                .Where(property => property.PropertyType.IsGenericType
                                    && property.PropertyType.GetGenericTypeDefinition() == typeof(Option<>)
                                    && property.PropertyType.GetGenericArguments()[0].IsGenericType
                                    && property.PropertyType.GetGenericArguments()[0].GetGenericTypeDefinition() == typeof(FrozenSet<>)
                                    && property.Name.EndsWith("NamesToExport", StringComparison.OrdinalIgnoreCase))
                .Select(property => property.Name)
                .Aggregate(jsonObject, addNameToExport);
    }

    private static JsonObject AddNamesToExport<T>(Option<FrozenSet<T>> names, JsonObject jsonObject) where T : ResourceName =>
        names.Map(names =>
        {
            var sectionName = ShouldExtractFactory.GetConfigurationSectionName<T>();
            var getNameToWrite = (T name) => (JsonNode?)ShouldExtractFactory.GetNameToFind(name);
            var namesToWrite = names.Select(getNameToWrite)
                                    .ToJsonArray();
            return jsonObject.SetProperty(sectionName, namesToWrite);
        }).IfNone(jsonObject);

    private JsonObject WriteDefaultApiSpecificationFormat(JsonObject jsonObject) =>
        DefaultApiSpecification.Map(specification =>
        {
            var key = Gen.OneOfConst("apiSpecificationFormat", "API_SPECIFICATION_FORMAT").Single();
            var value = specification switch
            {
                ApiSpecification.Wadl => "Wadl",
                ApiSpecification.OpenApi { Format: OpenApiFormat.Json, Version: OpenApiVersion.V2 } => "OpenAPIV2Json",
                ApiSpecification.OpenApi { Format: OpenApiFormat.Yaml, Version: OpenApiVersion.V2 } => "OpenAPIV2Yaml",
                ApiSpecification.OpenApi { Format: OpenApiFormat.Json, Version: OpenApiVersion.V3 } => Gen.OneOfConst("OpenAPIV3Json", "JSON").Single(),
                ApiSpecification.OpenApi { Format: OpenApiFormat.Yaml, Version: OpenApiVersion.V3 } => Gen.OneOfConst("OpenAPIV3Yaml", "YAML").Single(),
                _ => throw new InvalidOperationException($"Invalid type {specification}.")
            };

            return jsonObject.SetProperty(key, value);
        }).IfNone(jsonObject);

    public static bool ShouldExtract<T>(T name, Option<FrozenSet<T>> namesToExport) where T : ResourceName =>
        namesToExport.Match(names =>
        {
            var nameToFindString = ShouldExtractFactory.GetNameToFind(name);

            // Run T.From(nameToFindString)
            var nameToFind = Expression.Lambda<Func<T>>(Expression.Call(typeof(T), "From", [], Expression.Constant(nameToFindString))).Compile()();

            return names.Contains(nameToFind);
        }, () => true);
}

file sealed class RunExtractorHandler(ILogger<RunExtractor> logger,
                                      ActivitySource activitySource,
                                      GetSubscriptionId getSubscriptionId,
                                      GetResourceGroupName getResourceGroupName,
                                      GetBearerToken getBearerToken)
{
    public async ValueTask Handle(ExtractorOptions options, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(RunExtractor));

        logger.LogInformation("Running extractor...");

        var configurationFileOption = await TryGetConfigurationYamlFile(options, serviceDirectory, cancellationToken);
        var arguments = await GetArguments(serviceName, serviceDirectory, configurationFileOption, cancellationToken);
        await extractor.Program.Main(arguments);
    }

    private static async ValueTask<Option<FileInfo>> TryGetConfigurationYamlFile(ExtractorOptions extractorOptions, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        var optionsJson = extractorOptions.ToJsonObject();
        if (optionsJson.Count == 0)
        {
            return Option<FileInfo>.None;
        }

        var yamlFilePath = Path.Combine(serviceDirectory.ToDirectoryInfo().FullName, "configuration.extractor.yaml");
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

    private async ValueTask<string[]> GetArguments(ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, Option<FileInfo> configurationFileOption, CancellationToken cancellationToken)
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
#pragma warning restore CA1849 // Call async methods when in an async method

        return argumentDictionary.Aggregate(Array.Empty<string>(), (arguments, kvp) => [.. arguments, $"--{kvp.Key}", kvp.Value]);
    }

    private static string GetApiManagementServiceNameParameter() =>
        Gen.OneOfConst("API_MANAGEMENT_SERVICE_NAME", "apimServiceName").Single();
}

file sealed class ValidateExtractorArtifactsHandler(ILogger<ValidateExtractorArtifacts> logger,
                                                    ActivitySource activitySource,
                                                    ValidateExtractedNamedValues validateNamedValues,
                                                    ValidateExtractedTags validateTags,
                                                    ValidateExtractedVersionSets validateVersionSets,
                                                    ValidateExtractedBackends validateBackends,
                                                    ValidateExtractedLoggers validateLoggers,
                                                    ValidateExtractedDiagnostics validateDiagnostics,
                                                    ValidateExtractedPolicyFragments validatePolicyFragments,
                                                    ValidateExtractedServicePolicies validateServicePolicies,
                                                    ValidateExtractedGroups validateGroups,
                                                    ValidateExtractedProducts validateProducts,
                                                    ValidateExtractedApis validateApis)
{
    public async ValueTask Handle(ExtractorOptions options, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(ValidateExtractorArtifacts));

        logger.LogInformation("Validating extractor artifacts...");

        await validateNamedValues(options.NamedValueNamesToExport, serviceName, serviceDirectory, cancellationToken);
        await validateTags(options.TagNamesToExport, serviceName, serviceDirectory, cancellationToken);
        await validateVersionSets(options.VersionSetNamesToExport, serviceName, serviceDirectory, cancellationToken);
        await validateBackends(options.BackendNamesToExport, serviceName, serviceDirectory, cancellationToken);
        await validateLoggers(options.LoggerNamesToExport, serviceName, serviceDirectory, cancellationToken);
        await validateDiagnostics(options.DiagnosticNamesToExport, options.LoggerNamesToExport, serviceName, serviceDirectory, cancellationToken);
        await validatePolicyFragments(options.PolicyFragmentNamesToExport, serviceName, serviceDirectory, cancellationToken);
        await validateServicePolicies(serviceName, serviceDirectory, cancellationToken);
        await validateGroups(options.GroupNamesToExport, serviceName, serviceDirectory, cancellationToken);
        await validateProducts(options.ProductNamesToExport, serviceName, serviceDirectory, cancellationToken);
        await validateApis(options.ApiNamesToExport, options.DefaultApiSpecification, options.VersionSetNamesToExport, serviceName, serviceDirectory, cancellationToken);
    }
}

internal static class ExtractorServices
{
    public static void ConfigureRunExtractor(IServiceCollection services)
    {
        services.TryAddSingleton<RunExtractorHandler>();
        services.TryAddSingleton<RunExtractor>(provider => provider.GetRequiredService<RunExtractorHandler>().Handle);
    }

    public static void ConfigureValidateExtractorArtifacts(IServiceCollection services)
    {
        NamedValueServices.ConfigureValidateExtractedNamedValues(services);
        TagServices.ConfigureValidateExtractedTags(services);
        VersionSetServices.ConfigureValidateExtractedVersionSets(services);
        BackendServices.ConfigureValidateExtractedBackends(services);
        LoggerServices.ConfigureValidateExtractedLoggers(services);
        DiagnosticServices.ConfigureValidateExtractedDiagnostics(services);
        PolicyFragmentServices.ConfigureValidateExtractedPolicyFragments(services);
        ServicePolicyServices.ConfigureValidateExtractedServicePolicies(services);
        GroupServices.ConfigureValidateExtractedGroups(services);
        ProductServices.ConfigureValidateExtractedProducts(services);
        ApiServices.ConfigureValidateExtractedApis(services);

        services.TryAddSingleton<ValidateExtractorArtifactsHandler>();
        services.TryAddSingleton<ValidateExtractorArtifacts>(provider => provider.GetRequiredService<ValidateExtractorArtifactsHandler>().Handle);
    }
}