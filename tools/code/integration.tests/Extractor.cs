using common;
using common.tests;
using CsCheck;
using extractor;
using LanguageExt;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
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

public delegate ValueTask RunExtractor(ExtractorOptions options, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);
public delegate ValueTask ValidateExtractorArtifacts(ExtractorOptions options, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

public sealed record ExtractorOptions
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
    public required Option<FrozenSet<WorkspaceName>> WorkspaceNamesToExport { get; init; }

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
        VersionSetNamesToExport = Option<FrozenSet<VersionSetName>>.None,
        WorkspaceNamesToExport = Option<FrozenSet<WorkspaceName>>.None
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
            SubscriptionNamesToExport = subscriptions,
            WorkspaceNamesToExport = Option<FrozenSet<WorkspaceName>>.None
        };

    public static Gen<Option<FrozenSet<TName>>> GenerateOptionalNamesToExport<TName, TModel>(IEnumerable<TModel> models) =>
        GenerateNamesToExport<TName, TModel>(models).OptionOf();

    private static Gen<FrozenSet<TName>> GenerateNamesToExport<TName, TModel>(IEnumerable<TModel> models)
    {
        // Generate the function var modelToName = (TModel model) => model.Name
        var parameterExpression = Expression.Parameter(typeof(TModel), "model");
        var propertyExpression = Expression.Property(parameterExpression, "Name");
        var lambdaExpression = Expression.Lambda<Func<TModel, TName>>(propertyExpression, parameterExpression);
        var modelToName = lambdaExpression.Compile();

        return Generator.SubFrozenSetOf(models.Select(modelToName).ToArray());
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
            var sectionName = FindConfigurationNamesFactory.GetConfigurationSectionName<T>();
            var getNameToWrite = (T name) => (JsonNode?)FindConfigurationNamesFactory.GetNameToFind(name);
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
            var nameToFindString = FindConfigurationNamesFactory.GetNameToFind(name);

            // Run T.From(nameToFindString)
            var nameToFind = Expression.Lambda<Func<T>>(Expression.Call(typeof(T), "From", [], Expression.Constant(nameToFindString))).Compile()();

            return names.Contains(nameToFind);
        }, () => true);
}

public static class ExtractorModule
{
    public static void ConfigureRunExtractor(IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(GetRunExtractor);
    }

    private static RunExtractor GetRunExtractor(IServiceProvider provider)
    {
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (options, serviceName, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(RunExtractor));

            logger.LogInformation("Running extractor...");

            var configurationFileOption = await tryGetConfigurationYamlFile(options, serviceDirectory, cancellationToken);
            var arguments = getArguments(serviceName, serviceDirectory, configurationFileOption, cancellationToken);
            await extractor.Program.Main(arguments);
        };

        static async ValueTask<Option<FileInfo>> tryGetConfigurationYamlFile(ExtractorOptions extractorOptions, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
        {
            var optionsJson = extractorOptions.ToJsonObject();
            if (optionsJson.Count == 0)
            {
                return Option<FileInfo>.None;
            }

            var yamlFilePath = Path.Combine(serviceDirectory.ToDirectoryInfo().FullName, "configuration.extractor.yaml");
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

        static string[] getArguments(ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, Option<FileInfo> configurationFileOption, CancellationToken cancellationToken)
        {
            var argumentDictionary = new Dictionary<string, string>
            {
                [$"{getApiManagementServiceNameParameter()}"] = serviceName.ToString(),
                ["API_MANAGEMENT_SERVICE_OUTPUT_FOLDER_PATH"] = serviceDirectory.ToDirectoryInfo().FullName
            };

            configurationFileOption.Iter(file => argumentDictionary.Add("CONFIGURATION_YAML_PATH", file.FullName));

            return argumentDictionary.Aggregate(Array.Empty<string>(), (arguments, kvp) => [.. arguments, $"--{kvp.Key}", kvp.Value]);
        }

        static string getApiManagementServiceNameParameter() =>
            Gen.OneOfConst("API_MANAGEMENT_SERVICE_NAME", "apimServiceName").Single();
    }

    public static void ConfigureValidateExtractorArtifacts(IHostApplicationBuilder builder)
    {
        NamedValueModule.ConfigureValidateExtractedNamedValues(builder);
        TagModule.ConfigureValidateExtractedTags(builder);
        VersionSetModule.ConfigureValidateExtractedVersionSets(builder);
        BackendModule.ConfigureValidateExtractedBackends(builder);
        LoggerModule.ConfigureValidateExtractedLoggers(builder);
        DiagnosticModule.ConfigureValidateExtractedDiagnostics(builder);
        PolicyFragmentModule.ConfigureValidateExtractedPolicyFragments(builder);
        ServicePolicyModule.ConfigureValidateExtractedServicePolicies(builder);
        GroupModule.ConfigureValidateExtractedGroups(builder);
        ProductModule.ConfigureValidateExtractedProducts(builder);
        ApiModule.ConfigureValidateExtractedApis(builder);

        builder.Services.TryAddSingleton(GetValidateExtractorArtifacts);
    }

    private static ValidateExtractorArtifacts GetValidateExtractorArtifacts(IServiceProvider provider)
    {
        var validateNamedValues = provider.GetRequiredService<ValidateExtractedNamedValues>();
        var validateTags = provider.GetRequiredService<ValidateExtractedTags>();
        var validateVersionSets = provider.GetRequiredService<ValidateExtractedVersionSets>();
        var validateBackends = provider.GetRequiredService<ValidateExtractedBackends>();
        var validateLoggers = provider.GetRequiredService<ValidateExtractedLoggers>();
        var validateDiagnostics = provider.GetRequiredService<ValidateExtractedDiagnostics>();
        var validatePolicyFragments = provider.GetRequiredService<ValidateExtractedPolicyFragments>();
        var validateServicePolicies = provider.GetRequiredService<ValidateExtractedServicePolicies>();
        var validateGroups = provider.GetRequiredService<ValidateExtractedGroups>();
        var validateProducts = provider.GetRequiredService<ValidateExtractedProducts>();
        var validateApis = provider.GetRequiredService<ValidateExtractedApis>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (options, serviceName, serviceDirectory, cancellationToken) =>
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
        };
    }
}