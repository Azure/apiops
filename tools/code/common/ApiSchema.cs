using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public sealed record GraphQLSchemaFile : FileRecord
{
    public ApiDirectory ApiDirectory { get; }
    public const string FileName = "schema.graphql";

    private GraphQLSchemaFile(ApiDirectory apiDirectory)
        : base(apiDirectory.Path.Append(FileName))
    {
        ApiDirectory = apiDirectory;
    }

    public static GraphQLSchemaFile From(ApiDirectory apiDirectory) => new(apiDirectory);

    public static GraphQLSchemaFile? TryFrom(ServiceDirectory serviceDirectory, FileInfo file)
    {
        ApiDirectory? apiDirectory = ApiDirectory.TryFrom(serviceDirectory, file.Directory);

        return (apiDirectory, file.Name) switch
        {
            (not null, FileName) => new GraphQLSchemaFile(apiDirectory),
            _ => null
        };
    }
}

public sealed record ApiSchemaName : NonEmptyString
{
    private ApiSchemaName(string value) : base(value)
    {
    }

    public static ApiSchemaName From(string value) => new(value);

    public static ApiSchemaName GraphQLSchemaName() => new("graphql");
}


public static class ApiSchema
{

    internal static Uri GetUri(ServiceProviderUri serviceProviderUri, ServiceName serviceName, ApiName apiName, ApiSchemaName schemaName)
    {
        return Api.GetUri(serviceProviderUri, serviceName, apiName)
           .AppendPath("schemas")
           .AppendPath(schemaName);
    }

    public static async ValueTask<string?> TryGetGraphQLSchemaContent(Func<Uri, CancellationToken, ValueTask<JsonObject?>> tryGetResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, ApiName apiName, CancellationToken cancellationToken)
    {
        Uri uri = GetUri(serviceProviderUri,
                         serviceName,
                         apiName,
                         ApiSchemaName.GraphQLSchemaName());

        JsonObject? json = await tryGetResource(uri, cancellationToken);

        return json?.GetJsonObjectProperty("properties")
                    .GetJsonObjectProperty("document")
                    .GetStringProperty("value");
    }

    public static async ValueTask PutGraphQL(Func<Uri, JsonObject, CancellationToken, ValueTask> putResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, ApiName apiName, string schemaText, CancellationToken cancellationToken)
    {
        JsonObject json = new()
        {
            ["name"] = ApiSchemaName.GraphQLSchemaName().ToString(),
            ["properties"] = new JsonObject
            {
                ["contentType"] = "application/vnd.ms-azure-apim.graphql.schema",
                ["value"] = schemaText
            }
        };

        Uri uri = GetUri(serviceProviderUri, serviceName, apiName, ApiSchemaName.GraphQLSchemaName());
        await putResource(uri, json, cancellationToken);
    }
}
