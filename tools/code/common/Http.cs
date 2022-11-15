using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public static class HttpPipelineExtensions
{
    public static async IAsyncEnumerable<JsonObject> ListJsonObjects(this HttpPipeline pipeline, Uri uri, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Uri? nextLink = uri;

        while (nextLink is not null)
        {
            var responseJson = await pipeline.GetJsonObject(nextLink, cancellationToken);

            var values = responseJson.TryGetJsonArrayProperty("value")
                                     .Map(jsonArray => jsonArray.Choose(node => node as JsonObject))
                         ?? Enumerable.Empty<JsonObject>();

            foreach (var value in values)
            {
                yield return value;
            }

            nextLink = responseJson.TryGetStringProperty("nextLink")
                                   .Map(x => new Uri(x));
        }
    }

    public static async ValueTask<JsonObject> GetJsonObject(this HttpPipeline pipeline, Uri uri, CancellationToken cancellationToken)
    {
        var content = await pipeline.GetContent(uri, cancellationToken);

        return content.ToObjectFromJson<JsonObject>();
    }

    public static async ValueTask<BinaryData> GetContent(this HttpPipeline pipeline, Uri uri, CancellationToken cancellationToken)
    {
        var request = pipeline.CreateRequest(uri, RequestMethod.Get);

        var response = await pipeline.SendRequestAsync(request, cancellationToken);
        response.Validate(uri);

        return response.Content;
    }

    public static async ValueTask DeleteResource(this HttpPipeline pipeline, Uri uri, CancellationToken cancellationToken)
    {
        var request = pipeline.CreateRequest(uri, RequestMethod.Delete);
        var response = await pipeline.SendRequestAsync(request, cancellationToken);
        response.Validate(uri);
    }

    public static async ValueTask PutResource(this HttpPipeline pipeline, Uri uri, JsonObject resource, CancellationToken cancellationToken)
    {
        var request = pipeline.CreateRequest(uri, RequestMethod.Put);
        var resourceBytes = JsonSerializer.SerializeToUtf8Bytes(resource);
        request.Content = RequestContent.Create(resourceBytes);
        request.Headers.Add("Content-type", "application/json");

        var response = await pipeline.SendRequestAsync(request, cancellationToken);
        response.Validate(uri);
        await pipeline.WaitForLongRunningOperation(response, cancellationToken);
    }

    private static Request CreateRequest(this HttpPipeline pipeline, Uri uri, RequestMethod requestMethod)
    {
        var request = pipeline.CreateRequest();
        request.Uri.Reset(uri);
        request.Method = requestMethod;

        return request;
    }

    private static Response Validate(this Response response, Uri requestUri)
    {
        return response.IsError
            ? throw new InvalidOperationException($"HTTP request to URI {requestUri} failed with status code {response.Status}. Content is '{response.Content}'.")
            : response;
    }

    private static async ValueTask WaitForLongRunningOperation(this HttpPipeline pipeline, Response response, CancellationToken cancellationToken)
    {
        var updatedResponse = response;
        while ((updatedResponse.Status == ((int)HttpStatusCode.Accepted))
               && updatedResponse.Headers.TryGetValue("Location", out var locationHeaderValue)
               && Uri.TryCreate(locationHeaderValue, UriKind.Absolute, out var locationUri)
               && locationUri is not null)
        {
            if (updatedResponse.Headers.TryGetValue("Retry-After", out var retryAfterString) && int.TryParse(retryAfterString, out var retryAfterSeconds))
            {
                var retryAfterDuration = TimeSpan.FromSeconds(retryAfterSeconds);
                await Task.Delay(retryAfterDuration, cancellationToken);
            }
            else
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }

            var request = pipeline.CreateRequest(locationUri, RequestMethod.Get);
            updatedResponse = await pipeline.SendRequestAsync(request, cancellationToken);
            updatedResponse.Validate(locationUri);
        }
    }
}
