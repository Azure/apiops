using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace publisher;

internal delegate IAsyncEnumerable<JsonObject> ListRestResources(Uri uri, CancellationToken cancellationToken);

internal delegate ValueTask PutRestResource(Uri uri, JsonObject jsonObject, CancellationToken cancellationToken);

internal delegate ValueTask DeleteRestResource(Uri uri, CancellationToken cancellationToken);