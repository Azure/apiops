using System;

namespace common;

public static class UriExtensions
{
    public static Uri AppendPath(this Uri uri, string pathSegment)
    {
        return Flurl.GeneratedExtensions.AppendPathSegment(uri, pathSegment)
                                        .ToUri();
    }

    public static Uri SetQueryParameter(this Uri uri, string parameterName, string parameterValue)
    {
        return Flurl.GeneratedExtensions.SetQueryParam(uri, parameterName, parameterValue)
                                        .ToUri();
    }
}