using Microsoft.OpenApi;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal record OpenApiSpecification(OpenApiSpecVersion Version, OpenApiFormat Format);

internal delegate IAsyncEnumerable<JsonObject> ListRestResources(Uri uri, CancellationToken cancellationToken);

internal delegate ValueTask<JsonObject> GetRestResource(Uri uri, CancellationToken cancellationToken);

internal delegate ValueTask<Stream> DownloadResource(Uri uri, CancellationToken cancellationToken);