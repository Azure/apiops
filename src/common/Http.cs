using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public sealed record BatchRequest
{
    public required Uri Uri { get; init; }
    public required Guid Name { get; init; }
    public required HttpMethod Method { get; init; }
    public Option<IDictionary<string, string>> Headers { get; init; } = Option.None;
    public Option<JsonNode> Content { get; init; } = Option.None;
}

public static class HttpPipelineModule
{
    public static async ValueTask<Result<Option<ResponseHeaders>>> Head(this HttpPipeline pipeline, Uri uri, CancellationToken cancellationToken)
    {
        using var request = pipeline.CreateRequest(uri, RequestMethod.Head);

        using var response = await pipeline.SendRequestAsync(request, cancellationToken);

        return response switch
        {
            { IsError: false } => Option.Some(response.Headers),
            { IsError: true } when response.Status == (int)HttpStatusCode.NotFound => Option<ResponseHeaders>.None(),
            _ => Error.From(response.ToHttpRequestException(request.Uri.ToUri()))
        };
    }

    public static async IAsyncEnumerable<JsonObject> ListJsonObjects(this HttpPipeline pipeline, Uri uri, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Uri? nextLink = uri;

        while (nextLink is not null)
        {
            var jsonResult = from content in await pipeline.GetContent(nextLink, cancellationToken)
                             from jsonObject in JsonObjectModule.From(content)
                             from valueJsonArray in jsonObject.GetJsonArrayProperty("value")
                             from valueJsonObjects in valueJsonArray.GetJsonObjects()
                             let nextLinkUri = jsonObject.GetAbsoluteUriProperty("nextLink")
                                                         .IfErrorNull()
                             select (valueJsonObjects, nextLinkUri);

            (var values, nextLink) = jsonResult.IfErrorThrow();

            foreach (var value in values)
            {
                yield return value;
            }
        }
    }

    public static async ValueTask<Result<BinaryData>> GetContent(this HttpPipeline pipeline, Uri uri, CancellationToken cancellationToken)
    {
        using var request = pipeline.CreateRequest(uri, RequestMethod.Get);

        var result = await pipeline.GetResponse(request, cancellationToken);

        return result.Map(response =>
        {
            using (response)
            {
                return response.Content;
            }
        });
    }

    public static async ValueTask<Result<Option<BinaryData>>> GetOptionalContent(this HttpPipeline pipeline, Uri uri, CancellationToken cancellationToken)
    {
        var result = await pipeline.GetContent(uri, cancellationToken);

        return result.Map(Option.Some)
                     .IfError(error => error is Error.Exceptional exceptionalError
                                       && exceptionalError.Exception is HttpRequestException httpRequestException
                                       && httpRequestException.StatusCode == HttpStatusCode.NotFound
                                        ? Result<Option<BinaryData>>.Success(Option.None)
                                        : error);
    }

    private static Request CreateRequest(this HttpPipeline pipeline, Uri uri, RequestMethod method)
    {
        var request = pipeline.CreateRequest();
        request.Uri.Reset(uri);
        request.Method = method;

        return request;
    }

    private static async ValueTask<Result<Response>> GetResponse(this HttpPipeline pipeline, Request request, CancellationToken cancellationToken)
    {
        var response = await pipeline.SendRequestAsync(request, cancellationToken);

        return response.IsError
                ? Result.Error<Response>(response.ToHttpRequestException(request.Uri.ToUri()))
                : Result.Success(response);
    }

    public static async ValueTask<Result<Unit>> PutJson(this HttpPipeline pipeline, Uri uri, JsonNode content, CancellationToken cancellationToken)
    {
        using var request = pipeline.CreateRequest(uri, RequestMethod.Put);
        request.Content = BinaryData.FromObjectAsJson(content);
        request.Headers.Add("Content-Type", "application/json");

        var response = await pipeline.GetResponse(request, cancellationToken);

        return await response.BindTask(async response =>
        {
            using (response)
            {
                return await pipeline.WaitForLongRunningOperation(response, cancellationToken);
            }
        });
    }

    //public static async ValueTask BatchSend(this HttpPipeline pipeline, IEnumerable<BatchRequest> requests, CancellationToken cancellationToken)
    //{

    //}

    public static async ValueTask<Result<Unit>> PostJson(this HttpPipeline pipeline, Uri uri, JsonNode content, CancellationToken cancellationToken)
    {
        using var request = pipeline.CreateRequest(uri, RequestMethod.Post);
        request.Content = BinaryData.FromObjectAsJson(content);
        request.Headers.Add("Content-Type", "application/json");

        var response = await pipeline.GetResponse(request, cancellationToken);

        return await response.BindTask(async response =>
        {
            using (response)
            {
                return await pipeline.WaitForLongRunningOperation(response, cancellationToken);
            }
        });
    }

    private static async ValueTask<Result<Unit>> WaitForLongRunningOperation(this HttpPipeline pipeline, Response response, CancellationToken cancellationToken)
    {
        return await runUntilCompletion(response);

        async ValueTask<Result<Unit>> runUntilCompletion(Response response)
        {
            if (IsLongRunningOperationInProgress(response)
                && response.Headers.TryGetValue("Location", out var locationHeaderValue)
                && Uri.TryCreate(locationHeaderValue, UriKind.Absolute, out var locationUri)
                && locationUri is not null)
            {
                var retryAfterDuration = GetRetryAfterDuration(response);
                await Task.Delay(retryAfterDuration, cancellationToken);

                using var request = pipeline.CreateRequest(locationUri, RequestMethod.Get);
                var result = await pipeline.GetResponse(request, cancellationToken);
                return await result.BindTask(async response =>
                {
                    using (response)
                    {
                        return await runUntilCompletion(response);
                    }
                });
            }

            return Result.Success(Unit.Instance);
        }
    }

    private static bool IsLongRunningOperationInProgress(Response response) =>
        response.Status switch
        {
            (int)HttpStatusCode.Accepted => true,
            (int)HttpStatusCode.OK or (int)HttpStatusCode.Created => IsProvisioningInProgress(response),
            _ => false
        };

    private static bool IsProvisioningInProgress(Response response)
    {
        var result = from node in JsonNodeModule.From(response.Content)
                     from jsonObject in node.AsJsonObject()
                         // State can either be in ".ProvisioningState" or in "properties.ProvisioningState"
                     from state in jsonObject.GetStringProperty("ProvisioningState")
                                             .IfError(_ => from properties in jsonObject.GetJsonObjectProperty("properties")
                                                           from state in properties.GetStringProperty("ProvisioningState")
                                                           select state)
                     select state.Equals("InProgress", StringComparison.OrdinalIgnoreCase);

        return result.IfError(_ => false);
    }

    private static TimeSpan GetRetryAfterDuration(Response response) =>
        response.Headers.TryGetValue("Retry-After", out var retryAfterString) && int.TryParse(retryAfterString, out var retryAfterSeconds)
            ? TimeSpan.FromSeconds(retryAfterSeconds)
            : TimeSpan.FromSeconds(1); // Default to 1 second if no Retry-After header is present

    public static async ValueTask<Result<Unit>> Delete(this HttpPipeline pipeline, Uri uri, CancellationToken cancellationToken, bool ignoreNotFound = false, bool waitForCompletion = true)
    {
        using var request = pipeline.CreateRequest(uri, RequestMethod.Delete);

        using var response = await pipeline.SendRequestAsync(request, cancellationToken);

        return response switch
        {
            { IsError: false } =>
                waitForCompletion
                    ? await pipeline.WaitForLongRunningOperation(response, cancellationToken)
                    : Result.Success(Unit.Instance),
            { Status: (int)HttpStatusCode.NotFound } when ignoreNotFound =>
                Result.Success(Unit.Instance),
            _ =>
                Result.Error<Unit>(response.ToHttpRequestException(request.Uri.ToUri()))
        };
    }
}

public static class HttpRequestExtensions
{
    public static HttpRequestException ToHttpRequestException(this Response response, Uri requestUri) =>
        new(message: $"HTTP request to URI {requestUri} failed with status code {response.Status}. Content is '{response.Content}'.", inner: null, statusCode: (HttpStatusCode)response.Status);
}

public class CommonRetryPolicy : RetryPolicy
{
    protected override bool ShouldRetry(HttpMessage message, Exception? exception) =>
        base.ShouldRetry(message, exception) || ShouldRetryInner(message, exception);

    protected override async ValueTask<bool> ShouldRetryAsync(HttpMessage message, Exception? exception) =>
        await base.ShouldRetryAsync(message, exception) || ShouldRetryInner(message, exception);

    private static bool ShouldRetryInner(HttpMessage message, Exception? exception)
    {
        try
        {
            return
                (message, exception) switch
                {
                    ({ Response.Status: 422 or 409 }, _) when HasManagementApiRequestFailedError(message.Response) => true,
                    ({ Response.Status: 412 }, _) => true,
                    ({ Response.Status: 429 }, _) => true,
                    _ => false
                };
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool HasManagementApiRequestFailedError(Response response) =>
        TryGetErrorCode(response)
            .Where(code => code.Equals("ManagementApiRequestFailed", StringComparison.OrdinalIgnoreCase))
            .IsSome;

    private static Option<string> TryGetErrorCode(Response response)
    {
        try
        {
            var result = from node in JsonNodeModule.From(response.Content)
                         from jsonObject in node.AsJsonObject()
                         from errorJsonObject in jsonObject.GetJsonObjectProperty("error")
                         from errorCode in errorJsonObject.GetStringProperty("code")
                         select errorCode;

            return result.ToOption();
        }
        catch (Exception exception) when (exception is ArgumentNullException or NotSupportedException or JsonException)
        {
            return Option.None;
        }
    }
}

public sealed class LoggingPolicy(ILogger logger) : HttpPipelinePolicy
{
    public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
    {
        ProcessAsync(message, pipeline).AsTask().GetAwaiter().GetResult();
    }

    public override async ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
    {
        if (logger.IsEnabled(LogLevel.Trace))
        {
            logger.LogTrace("""
                            Starting request
                            Method: {HttpMethod}
                            Uri: {Uri}
                            Content: {RequestContent}
                            """, message.Request.Method, message.Request.Uri, await GetRequestContent(message, message.CancellationToken));
        }
        else if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug("""
                            Starting request
                            Method: {HttpMethod}
                            Uri: {Uri}
                            """, message.Request.Method, message.Request.Uri);
        }

        var startTime = Stopwatch.GetTimestamp();
        await ProcessNextAsync(message, pipeline);
        var endTime = Stopwatch.GetTimestamp();
        var duration = TimeSpan.FromSeconds((endTime - startTime) / (double)Stopwatch.Frequency);

        if (logger.IsEnabled(LogLevel.Trace))
        {
            logger.LogTrace("""
                            Received response
                            Method: {HttpMethod}
                            Uri: {Uri}
                            Status code: {StatusCode}
                            Duration (hh:mm:ss): {Duration}
                            Content: {ResponseContent}
                            """, message.Request.Method, message.Request.Uri, message.Response.Status, duration.ToString("c"), GetResponseContent(message));
        }
        else if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug("""
                            Received response
                            Method: {HttpMethod}
                            Uri: {Uri}
                            Status code: {StatusCode}
                            Duration (hh:mm:ss): {Duration}
                            """, message.Request.Method, message.Request.Uri, message.Response.Status, duration.ToString("c"));
        }
    }

    private static async ValueTask<string> GetRequestContent(HttpMessage message, CancellationToken cancellationToken)
    {
        if (message.Request.Content is null)
        {
            return "<null>";
        }
        else if (HeaderIsJson(message.Request.Headers))
        {
            using var stream = new MemoryStream();
            await message.Request.Content.WriteToAsync(stream, cancellationToken);
            stream.Position = 0;
            var data = await BinaryData.FromStreamAsync(stream, cancellationToken);

            return data.ToString();
        }
        else
        {
            return "<non-json>";
        }
    }

    private static bool HeaderIsJson(IEnumerable<HttpHeader> headers) =>
        headers.Any(header => header.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
                                && header.Value.Contains("application/json", StringComparison.OrdinalIgnoreCase));

    private static string GetResponseContent(HttpMessage message) =>
        message.Response.Content is null
        ? "<null>"
        : HeaderIsJson(message.Response.Headers)
            ? message.Response.Content.ToString()
            : "<non-json>";
}

public class TelemetryPolicy(Version version) : HttpPipelinePolicy
{
    public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
    {
        ProcessAsync(message, pipeline).AsTask().GetAwaiter().GetResult();
    }

    public override async ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
    {
        var header = new ProductHeaderValue("apimanagement-apiops", version.ToString());
        message.Request.Headers.Add(HttpHeader.Names.UserAgent, header.ToString());

        await ProcessNextAsync(message, pipeline);
    }
}