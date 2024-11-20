using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;

namespace common;

public static class AzureModule
{
    public static Func<HttpRequestException, IAsyncEnumerable<T>> GetMethodNotAllowedInPricingTierHandler<T>() =>
        exception =>
        exception switch
        {
            { StatusCode: HttpStatusCode.BadRequest } when exception.Message.Contains("MethodNotAllowedInPricingTier", StringComparison.OrdinalIgnoreCase) => AsyncEnumerable.Empty<T>(),
            _ => throw exception
        };
}
