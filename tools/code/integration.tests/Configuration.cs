//using Azure.Core;
//using Azure.Core.Pipeline;
//using Azure.Identity;
//using Azure.ResourceManager;
//using common;
//using Flurl;
//using LanguageExt;
//using LanguageExt.UnsafeValueAccess;
//using Microsoft.Extensions.Configuration;
//using Microsoft.Extensions.Logging;
//using Microsoft.IdentityModel.JsonWebTokens;
//using System;
//using System.Threading;
//using System.Threading.Tasks;

//namespace integration.tests;

//internal static class Configuration
//{
//    private static readonly Lazy<IConfiguration> @default = new(() => CreateDefault());
//    public static IConfiguration Default => @default.Value;

//    private static readonly Lazy<AzureEnvironment> azureEnvironment = new(() => GetAzureEnvironment(Default));
//    private static AzureEnvironment AzureEnvironment => azureEnvironment.Value;

//    private static readonly Lazy<string> location = new(() => GetLocation(Default));
//    public static string Location => location.Value;

//    private static readonly Lazy<Uri> apimProviderUri = new(() => GetApimProviderUri(Default, AzureEnvironment));
//    public static Uri ApimProviderUri => apimProviderUri.Value;

//    private static readonly Lazy<Uri> managementServiceProviderUri = new(() => GetManagementServiceProviderUri(ApimProviderUri));
//    public static Uri ManagementServiceProviderUri => managementServiceProviderUri.Value;

//    private static readonly Lazy<string> subscriptionId = new(() => GetSubscriptionId(Default));
//    public static string SubscriptionId => subscriptionId.Value;

//    private static readonly Lazy<string> resourceGroupName = new(() => GetResourceGroupName(Default));
//    public static string ResourceGroupName => resourceGroupName.Value;

//    private static readonly Lazy<TokenCredential> tokenCredential = new(() => GetTokenCredential(AzureEnvironment.AuthorityHost, Default));
//    private static TokenCredential TokenCredential => tokenCredential.Value;

//    private static readonly Lazy<HttpPipeline> httpPipeline = new(() => GetHttpPipeline(TokenCredential, AzureEnvironment));
//    public static HttpPipeline HttpPipeline => httpPipeline.Value;

//    private static readonly Lazy<ManagementServiceName> firstManagementServiceName = new(() => GetFirstManagementServiceName(Default));
//    public static ManagementServiceName FirstServiceName => firstManagementServiceName.Value;

//    private static readonly Lazy<ManagementServiceUri> firstServiceUri = new(() => GetManagementServiceUri(FirstServiceName));
//    public static ManagementServiceUri FirstServiceUri => firstServiceUri.Value;

//    private static readonly Lazy<ManagementServiceName> secondManagementServiceName = new(() => GetSecondManagementServiceName(Default));
//    public static ManagementServiceName SecondServiceName => secondManagementServiceName.Value;

//    private static readonly Lazy<ManagementServiceUri> secondServiceUri = new(() => GetManagementServiceUri(SecondServiceName));
//    public static ManagementServiceUri SecondServiceUri => secondServiceUri.Value;

//    private static IConfiguration CreateDefault() =>
//        new ConfigurationBuilder().AddEnvironmentVariables()
//                                  .AddUserSecrets(typeof(Extractor).Assembly)
//                                  .Build();

//    private static AzureEnvironment GetAzureEnvironment(IConfiguration configuration) =>
//        configuration.TryGetValue("AZURE_CLOUD_ENVIRONMENT").ValueUnsafe() switch
//        {
//            null => AzureEnvironment.Public,
//            "AzureGlobalCloud" or nameof(ArmEnvironment.AzurePublicCloud) => AzureEnvironment.Public,
//            "AzureChinaCloud" or nameof(ArmEnvironment.AzureChina) => AzureEnvironment.China,
//            "AzureUSGovernment" or nameof(ArmEnvironment.AzureGovernment) => AzureEnvironment.USGovernment,
//            "AzureGermanCloud" or nameof(ArmEnvironment.AzureGermany) => AzureEnvironment.Germany,
//            _ => throw new InvalidOperationException($"AZURE_CLOUD_ENVIRONMENT is invalid. Valid values are {nameof(ArmEnvironment.AzurePublicCloud)}, {nameof(ArmEnvironment.AzureChina)}, {nameof(ArmEnvironment.AzureGovernment)}, {nameof(ArmEnvironment.AzureGermany)}")
//        };

//    private static Uri GetApimProviderUri(IConfiguration configuration, AzureEnvironment azureEnvironment)
//    {
//        var apiVersion = configuration.TryGetValue("ARM_API_VERSION")
//                                      .IfNone(() => "2022-08-01");

//        return azureEnvironment.ManagementEndpoint
//                               .AppendPathSegment("subscriptions")
//                               .AppendPathSegment(GetSubscriptionId(configuration))
//                               .AppendPathSegment("resourceGroups")
//                               .AppendPathSegment(GetResourceGroupName(configuration))
//                               .AppendPathSegment("providers/Microsoft.ApiManagement")
//                               .SetQueryParam("api-version", apiVersion)
//                               .ToUri();
//    }

//    private static Uri GetManagementServiceProviderUri(Uri apimProviderUri) =>
//        apimProviderUri.AppendPathSegment("service").ToUri();

//    public static ManagementServiceUri GetManagementServiceUri(ManagementServiceName serviceName) =>
//        GetManagementServiceUri(serviceName, ManagementServiceProviderUri);

//    private static ManagementServiceUri GetManagementServiceUri(ManagementServiceName serviceName, Uri managementServiceProviderUri)
//    {
//        var uri = managementServiceProviderUri.AppendPathSegment(serviceName.ToString())
//                                              .ToUri();

//        return ManagementServiceUri.From(uri);
//    }

//    private static string GetLocation(IConfiguration configuration) =>
//        configuration.TryGetValue("AZURE_LOCATION").IfNone("westus");

//    private static string GetSubscriptionId(IConfiguration configuration) =>
//        configuration.GetValue("AZURE_SUBSCRIPTION_ID");

//    private static string GetResourceGroupName(IConfiguration configuration) =>
//        configuration.GetValue("AZURE_RESOURCE_GROUP_NAME");

//    private static TokenCredential GetTokenCredential(Uri azureAuthorityHost, IConfiguration configuration) =>
//        configuration.TryGetValue("AZURE_BEARER_TOKEN")
//                     .Map(GetCredentialFromToken)
//                     .IfNone(() => GetDefaultAzureCredential(azureAuthorityHost));

//    private static DefaultAzureCredential GetDefaultAzureCredential(Uri azureAuthorityHost) =>
//        new(new DefaultAzureCredentialOptions
//        {
//            AuthorityHost = azureAuthorityHost,
//            ExcludeVisualStudioCredential = true
//        });

//    private static HttpPipeline GetHttpPipeline(TokenCredential tokenCredential, AzureEnvironment azureEnvironment)
//    {
//        var clientOptions = ClientOptions.Default;
//        clientOptions.RetryPolicy = new RetryPolicy();
//        var bearerAuthenticationPolicy = new BearerTokenAuthenticationPolicy(tokenCredential, azureEnvironment.DefaultScope);

//#pragma warning disable CA2000 // Dispose objects before losing scope
//        var logger = LoggerFactory.Create(builder =>
//        {
//            builder.AddDebug().AddConsole();
//            builder.SetMinimumLevel(LogLevel.Trace);
//        }).CreateLogger<HttpPipeline>();
//#pragma warning restore CA2000 // Dispose objects before losing scope
//        var loggingPolicy = new ILoggerHttpPipelinePolicy(logger);

//        return HttpPipelineBuilder.Build(clientOptions, bearerAuthenticationPolicy, loggingPolicy);
//    }

//    private static TokenCredential GetCredentialFromToken(string token)
//    {
//        var jsonWebToken = new JsonWebToken(token);
//        var expirationDate = new DateTimeOffset(jsonWebToken.ValidTo);
//        var accessToken = new AccessToken(token, expirationDate);

//        return DelegatedTokenCredential.Create((context, cancellationToken) => accessToken);
//    }

//    public static async ValueTask<string> GetBearerToken(CancellationToken cancellationToken) =>
//        await GetBearerToken(TokenCredential, AzureEnvironment, cancellationToken);

//    private static async ValueTask<string> GetBearerToken(TokenCredential tokenCredential, AzureEnvironment azureEnvironment, CancellationToken cancellationToken)
//    {
//        var context = new TokenRequestContext([azureEnvironment.DefaultScope]);

//        var token = await tokenCredential.GetTokenAsync(context, cancellationToken);

//        return token.Token;
//    }

//    private static ManagementServiceName GetFirstManagementServiceName(IConfiguration configuration) =>
//        ManagementServiceName.From(configuration.GetValue("FIRST_API_MANAGEMENT_SERVICE_NAME"));

//    private static ManagementServiceName GetSecondManagementServiceName(IConfiguration configuration) =>
//        ManagementServiceName.From(configuration.GetValue("SECOND_API_MANAGEMENT_SERVICE_NAME"));
//}