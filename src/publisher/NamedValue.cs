using common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace publisher;

internal delegate ValueTask PutNamedValue(ResourceKey resourceKey, JsonObject dto, CancellationToken cancellationToken);


internal static partial class ResourceModule
{
    private static readonly string namedValueSemaphoreKey = Guid.NewGuid().ToString();

    public static void ConfigurePutNamedValue(IHostApplicationBuilder builder)
    {
        common.ResourceModule.ConfigurePutResourceInApim(builder);
        ConfigureNamedValueSemaphore(builder);

        builder.TryAddSingleton(ResolvePutNamedValue);
    }

    internal static PutNamedValue ResolvePutNamedValue(IServiceProvider provider)
    {
        var putResourceInApim = provider.GetRequiredService<PutResourceInApim>();
        var semaphore = provider.GetRequiredKeyedService<SemaphoreSlim>(namedValueSemaphoreKey);

        return async (resourceKey, dto, cancellationToken) =>
        {
            IResourceWithDto resource = resourceKey.Resource switch
            {
                NamedValueResource namedValueResource => namedValueResource,
                WorkspaceNamedValueResource workspaceNamedValueResource => workspaceNamedValueResource,
                _ => throw new ArgumentException($"Resource key '{resourceKey}' does not refer to a named value.", nameof(resourceKey))
            };

            var (name, parents) = (resourceKey.Name, resourceKey.Parents);

            // Put named values one at a time. APIM re-validates all references when a named value's display name changes.
            // Parallel processing can cause race conditions in a scenario like this:
            // - Policy X references named values with display names {{a-display}} and {{b-display}}.
            // - Publisher PUTs named value A, renaming "a-display" to "a-display-2".
            // - Publisher simultaneously PUTs named value B, renaming "b-display" to "b-display-2".
            // - APIM processes A's rename:
            //   - Validates all policies
            //   - Auto-renames {{a-display}} references to {{a-display-2}}.
            // - APIM processes B's rename concurrently:
            //   - Validates all policies
            //   - Catches policy X before its autorename to {{a-display-2}} is done.
            //   - Throws an error saying that policy X is referencing {{a-display}}, which no longer exists.
            await semaphore.WaitAsync(cancellationToken);

            try
            {
                await putResourceInApim(resource, name, dto, parents, cancellationToken);
            }
            finally
            {
                semaphore.Release();
            }
        };
    }

    private static void ConfigureNamedValueSemaphore(IHostApplicationBuilder builder)
    {
        builder.Services.TryAddKeyedSingleton(namedValueSemaphoreKey, (provider, _) => new SemaphoreSlim(1, 1));
    }
}