using Azure.Core.Pipeline;
using common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace publisher;

internal delegate ValueTask PutProductApi(ResourceName name, ResourceAncestors ancestors, JsonObject dto, CancellationToken cancellationToken);

internal static class ProductApiModule
{
    public static void ConfigurePutProductApi(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureServiceUri(builder);
        ManagementServiceModule.ConfigureServiceDirectory(builder);
        AzureModule.ConfigureHttpPipeline(builder);
        GitModule.ConfigureListServiceDirectoryFilesModifiedByCurrentCommit(builder);
        GitModule.ConfigureReadPreviousCommitFile(builder);
        GitModule.ConfigureGetPreviousCommitSubDirectories(builder);
        ResourceModule.ConfigureParseFile(builder);
        builder.TryAddSingleton(GetPutProductApi);
    }

    private static PutProductApi GetPutProductApi(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ServiceUri>();
        var serviceDirectory = provider.GetRequiredService<ServiceDirectory>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var listFilesModifiedByCommit = provider.GetRequiredService<ListServiceDirectoryFilesModifiedByCurrentCommit>();
        var parseFile = provider.GetRequiredService<ParseFile>();
        var readPreviousCommitFile = provider.GetRequiredService<ReadPreviousCommitFile>();
        var getPreviousCommitSubDirectories = provider.GetRequiredService<GetPreviousCommitSubDirectories>();
        var logger = provider.GetRequiredService<ILogger>();

        var resource = ProductApiResource.Instance;

        return async (name, ancestors, dto, cancellationToken) =>
        {
            await deleteExistingLinks(name, ancestors, dto, cancellationToken);
            await resource.PutDto(name, dto, ancestors, serviceUri, pipeline, cancellationToken);
        };

        async ValueTask deleteExistingLinks(ResourceName name, ResourceAncestors ancestors, JsonObject dto, CancellationToken cancellationToken)
        {
            var apiName = getApiName(dto);
            await deleteLinksWithApi(apiName, cancellationToken);
        }

        ResourceName getApiName(JsonObject dto)
        {
            var result = from properties in dto.GetJsonObjectProperty("properties")
                         from id in properties.GetStringProperty(resource.DtoPropertyNameForLinkedResource)
                         let apiNameString = id.Split('/').LastOrDefault()
                         from apiName in ResourceName.From(apiNameString)
                         select apiName;

            return result.IfErrorThrow();
        }

        async ValueTask deleteLinksWithApi(ResourceName apiName, CancellationToken cancellationToken) =>
            await listFilesModifiedByCommit()
                    // Get files that were deleted in the commit
                    .Bind(dictionary => dictionary.Find(GitAction.Delete))
                    .IfNone(() => [])
                    // Get product API resources from the files
                    .Choose(async file => from x in await parseFile(file, readPreviousCommitFile.Invoke, cancellationToken)
                                          where x.resource is ProductApiResource
                                          select (Name: x.name, Ancestors: x.ancestors))
                    // Get resources that match the API name
                    .Choose(async x => from dto in await resource.GetInformationFileDto(x.Name, x.Ancestors, serviceDirectory, getPreviousCommitSubDirectories.Invoke, readPreviousCommitFile.Invoke, cancellationToken)
                                       let dtoApiName = getApiName(dto)
                                       where dtoApiName == apiName
                                       select x)
                    // Delete the resources
                    .IterTaskParallel(async x =>
                                      {
                                          logger.LogInformation("Deleting existing {Resource} '{Name}'{Ancestors}...", ((IResource)resource).SingularName, x.Name, x.Ancestors.ToLogString());
                                          await resource.Delete(x.Name, x.Ancestors, serviceUri, pipeline, cancellationToken, ignoreNotFound: true, waitForCompletion: true);
                                      }, maxDegreeOfParallelism: Option.None, cancellationToken);
    }
}