using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Flurl;
using LanguageExt;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Polly.Retry;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public sealed record AzureEnvironment(Uri AuthorityHost, string DefaultScope, Uri ManagementEndpoint)
{
    public static AzureEnvironment Public { get; } = new(AzureAuthorityHosts.AzurePublicCloud, ArmEnvironment.AzurePublicCloud.DefaultScope, ArmEnvironment.AzurePublicCloud.Endpoint);

    public static AzureEnvironment USGovernment { get; } = new(AzureAuthorityHosts.AzureGovernment, ArmEnvironment.AzureGovernment.DefaultScope, ArmEnvironment.AzureGovernment.Endpoint);

    public static AzureEnvironment Germany { get; } = new(AzureAuthorityHosts.AzureGermany, ArmEnvironment.AzureGermany.DefaultScope, ArmEnvironment.AzureGermany.Endpoint);

    public static AzureEnvironment China { get; } = new(AzureAuthorityHosts.AzureChina, ArmEnvironment.AzureChina.DefaultScope, ArmEnvironment.AzureChina.Endpoint);
}

public class ApimHttpClient(HttpClient client)
{
    public HttpClient HttpClient { get; } = client;
}

public static class ApimHttpClientExtensions
{
    public static IServiceCollection ConfigureApimHttpClient(this IServiceCollection services)
    {
        services.AddHttpClient<ApimHttpClient>()
                .AddHttpMessageHandler<LoggingHandler>()
                .AddHttpMessageHandler<TokenCredentialHandler>()
                .AddStandardResilienceHandler(ConfigureResilienceOptions);

        services.TryAddTransient<LoggingHandler>();
        services.TryAddTransient<TokenCredentialHandler>();

        return services;
    }

    private static void ConfigureResilienceOptions(HttpStandardResilienceOptions options)
    {
        options.Retry.ShouldHandle = ShouldRetry;
    }

    private static async ValueTask<bool> ShouldRetry(RetryPredicateArguments<HttpResponseMessage> arguments) =>
        HttpClientResiliencePredicates.IsTransient(arguments.Outcome)
        || arguments.Outcome switch
        {
            { Result: { } response } =>
                await HasManagementApiRequestFailed(response, arguments.Context.CancellationToken)
                || await IsEntityNotFound(response, arguments.Context.CancellationToken),
            _ => false
        };

    private static async ValueTask<bool> HasManagementApiRequestFailed(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var responseJsonOption = await Common.TryGetJsonObjectCopy(response.Content, cancellationToken);

            return responseJsonOption.Bind(responseJson => responseJson.TryGetJsonObjectProperty("error")
                                                                       .Bind(error => error.TryGetStringProperty("code"))
                                                                       .ToOption()
                                                                       .Where(code => code.Equals("ManagementApiRequestFailed", StringComparison.OrdinalIgnoreCase)))
                                     .IsSome;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async ValueTask<bool> IsEntityNotFound(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode is not HttpStatusCode.BadRequest)
        {
            return false;
        }

        var content = await Common.GetStringCopy(response.Content, cancellationToken);
        return content.Contains("Entity with specified identifier not found", StringComparison.OrdinalIgnoreCase);
    }
}

# pragma warning disable CA1812
file sealed class LoggingHandler(ILoggerFactory loggerFactory) : DelegatingHandler
{
    private readonly ILogger logger = loggerFactory.CreateLogger(nameof(ApimHttpClient));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await LogRequest(request, cancellationToken);

        var stopWatch = Stopwatch.StartNew();
        var response = await base.SendAsync(request, cancellationToken);
        stopWatch.Stop();

        await LogResponse(response, stopWatch.Elapsed, cancellationToken);

        return response;
    }

    private async ValueTask LogRequest(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (logger.IsEnabled(LogLevel.Trace))
        {
            logger.LogTrace("""
                            Starting request
                            Method: {HttpMethod}
                            Uri: {Uri}
                            Content: {RequestContent}
                            """,
                            request.Method,
                            request.RequestUri,
                            await GetContentString(request.Content, request.Headers, cancellationToken));
        }
        else if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug("""
                            Starting request
                            Method: {HttpMethod}
                            Uri: {Uri}
                            """,
                            request.Method,
                            request.RequestUri);
        }
    }

    private static async ValueTask<string> GetContentString(HttpContent? content, HttpHeaders headers, CancellationToken cancellationToken) =>
        content switch
        {
            null => "<null>",
            _ => HeaderIsJson(headers)
                    ? await Common.GetStringCopy(content, cancellationToken)
                    : "<non-json>"
        };

    private static bool HeaderIsJson(HttpHeaders headers) =>
        headers.TryGetValues("Content-Type", out var values) &&
        values.Contains("application/json", StringComparer.OrdinalIgnoreCase);

    private async ValueTask LogResponse(HttpResponseMessage response, TimeSpan duration, CancellationToken cancellationToken)
    {
        if (logger.IsEnabled(LogLevel.Trace))
        {
            logger.LogTrace("""
                            Starting request
                            Method: {HttpMethod}
                            Uri: {Uri}
                            Duration (hh:mm:ss): {Duration}
                            Content: {RequestContent}
                            """,
                            response.RequestMessage?.Method,
                            response.RequestMessage?.RequestUri,
                            duration.ToString("c"),
                            await GetContentString(response.Content, response.Headers, cancellationToken));
        }
        else if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug("""
                            Starting request
                            Method: {HttpMethod}
                            Duration (hh:mm:ss): {Duration}
                            Uri: {Uri}
                            """,
                            response.RequestMessage?.Method,
                            response.RequestMessage?.RequestUri,
                            duration.ToString("c"));
        }
    }
}

file sealed class TokenCredentialHandler(TokenCredential tokenCredential) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is null)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        request.Headers.Authorization = await GetAuthenticationHeader(request.RequestUri, cancellationToken);

        return await base.SendAsync(request, cancellationToken);
    }

    private async ValueTask<AuthenticationHeaderValue> GetAuthenticationHeader(Uri uri, CancellationToken cancellationToken)
    {
        var accessToken = await GetAccessToken(uri, cancellationToken);

        return new AuthenticationHeaderValue("Bearer", accessToken.Token);
    }

    private async ValueTask<AccessToken> GetAccessToken(Uri uri, CancellationToken cancellationToken)
    {
        var scopeUrl = uri.GetLeftPart(UriPartial.Authority)
                          .AppendPathSegment(".default")
                          .ToString();

        return await GetAccessToken([scopeUrl], cancellationToken);
    }

    private async ValueTask<AccessToken> GetAccessToken(string[] scopes, CancellationToken cancellationToken)
    {
        var context = new TokenRequestContext(scopes);

        return await tokenCredential.GetTokenAsync(context, cancellationToken);
    }
}
#pragma warning restore CA1812

file static class Common
{
    public static async ValueTask<Stream> GetStreamCopy(HttpContent content, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        await content.CopyToAsync(stream, cancellationToken);
        stream.Position = 0;

        return stream;
    }

    public static async ValueTask<string> GetStringCopy(HttpContent content, CancellationToken cancellationToken)
    {
        using var stream = await GetStreamCopy(content, cancellationToken);
        var data = await BinaryData.FromStreamAsync(stream, cancellationToken);

        return data.ToString();
    }

    public static async ValueTask<Option<JsonObject>> TryGetJsonObjectCopy(HttpContent content, CancellationToken cancellationToken)
    {
        using var stream = await GetStreamCopy(content, cancellationToken);

        try
        {
            var node = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken);

            return node is JsonObject jsonObject
                    ? Option<JsonObject>.Some(jsonObject)
                    : Option<JsonObject>.None;
        }
        catch (JsonException)
        {
            return Option<JsonObject>.None;
        }
    }
}