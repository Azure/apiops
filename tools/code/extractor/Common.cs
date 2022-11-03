using Microsoft.OpenApi;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal abstract record DefaultApiSpecification
{
    public record Wadl : DefaultApiSpecification { }

    public record OpenApi(OpenApiSpecVersion Version, OpenApiFormat Format) : DefaultApiSpecification { }
}

internal delegate IAsyncEnumerable<JsonObject> ListRestResources(Uri uri, CancellationToken cancellationToken);

internal delegate ValueTask<JsonObject> GetRestResource(Uri uri, CancellationToken cancellationToken);

internal delegate ValueTask<Stream> DownloadResource(Uri uri, CancellationToken cancellationToken);