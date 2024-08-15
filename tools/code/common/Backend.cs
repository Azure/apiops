using Azure.Core.Pipeline;
using Flurl;
using LanguageExt;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public sealed record BackendName : ResourceName, IResourceName<BackendName>
{
    private BackendName(string value) : base(value) { }

    public static BackendName From(string value) => new(value);
}

public sealed record BackendsUri : ResourceUri
{
    public required ManagementServiceUri ServiceUri { get; init; }

    private static string PathSegment { get; } = "backends";

    protected override Uri Value =>
        ServiceUri.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static BackendsUri From(ManagementServiceUri serviceUri) =>
        new() { ServiceUri = serviceUri };
}

public sealed record BackendUri : ResourceUri
{
    public required BackendsUri Parent { get; init; }

    public required BackendName Name { get; init; }

    protected override Uri Value =>
        Parent.ToUri().AppendPathSegment(Name.ToString()).ToUri();

    public static BackendUri From(BackendName name, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = BackendsUri.From(serviceUri),
            Name = name
        };
}

public sealed record BackendsDirectory : ResourceDirectory
{
    public required ManagementServiceDirectory ServiceDirectory { get; init; }

    private static string Name { get; } = "backends";

    protected override DirectoryInfo Value =>
        ServiceDirectory.ToDirectoryInfo().GetChildDirectory(Name);

    public static BackendsDirectory From(ManagementServiceDirectory serviceDirectory) =>
        new() { ServiceDirectory = serviceDirectory };

    public static Option<BackendsDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        directory?.Name == Name &&
        directory?.Parent?.FullName == serviceDirectory.ToDirectoryInfo().FullName
            ? new BackendsDirectory { ServiceDirectory = serviceDirectory }
            : Option<BackendsDirectory>.None;
}

public sealed record BackendDirectory : ResourceDirectory
{
    public required BackendsDirectory Parent { get; init; }

    public required BackendName Name { get; init; }

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name.Value);

    public static BackendDirectory From(BackendName name, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = BackendsDirectory.From(serviceDirectory),
            Name = name
        };

    public static Option<BackendDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        from parent in BackendsDirectory.TryParse(directory?.Parent, serviceDirectory)
        let name = BackendName.From(directory!.Name)
        select new BackendDirectory
        {
            Parent = parent,
            Name = name
        };
}

public sealed record BackendInformationFile : ResourceFile
{
    public required BackendDirectory Parent { get; init; }

    public static string Name { get; } = "backendInformation.json";

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile(Name);

    public static BackendInformationFile From(BackendName name, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = BackendDirectory.From(name, serviceDirectory)
        };

    public static Option<BackendInformationFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        file is not null &&
        file.Name == Name
            ? from parent in BackendDirectory.TryParse(file.Directory, serviceDirectory)
              select new BackendInformationFile
              {
                  Parent = parent
              }
            : Option<BackendInformationFile>.None;
}

public sealed record BackendDto
{
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public required BackendContract Properties { get; init; }

    public record BackendContract
    {
        [JsonPropertyName("circuitBreaker")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public BackendCircuitBreaker? CircuitBreaker { get; init; }

        [JsonPropertyName("credentials")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public BackendCredentialsContract? Credentials { get; init; }

        [JsonPropertyName("description")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Description { get; init; }

        [JsonPropertyName("pool")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public Pool? Pool { get; init; }

        [JsonPropertyName("properties")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public BackendProperties? Properties { get; init; }

        [JsonPropertyName("protocol")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Protocol { get; init; }

        [JsonPropertyName("proxy")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public BackendProxyContract? Proxy { get; init; }

        [JsonPropertyName("resourceId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? ResourceId { get; init; }

        [JsonPropertyName("title")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Title { get; init; }

        [JsonPropertyName("tls")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public BackendTlsProperties? Tls { get; init; }

        [JsonPropertyName("type")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Type { get; init; }

        [JsonPropertyName("url")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
#pragma warning disable CA1056 // URI-like properties should not be strings
        public string? Url { get; init; }
#pragma warning restore CA1056 // URI-like properties should not be strings
    }

    public record BackendCircuitBreaker
    {
        [JsonPropertyName("rules")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public ImmutableArray<CircuitBreakerRule>? Rules { get; init; }
    }

    public record CircuitBreakerRule
    {
        [JsonPropertyName("acceptRetryAfter")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool? AcceptRetryAfter { get; init; }

        [JsonPropertyName("failureCondition")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public CircuitBreakerFailureCondition? FailureCondition { get; init; }

        [JsonPropertyName("name")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Name { get; init; }

        [JsonPropertyName("tripDuration")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? TripDuration { get; init; }
    }

    public record CircuitBreakerFailureCondition
    {
        [JsonPropertyName("count")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int? Count { get; init; }

        [JsonPropertyName("errorReasons")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public ImmutableArray<string>? ErrorReasons { get; init; }

        [JsonPropertyName("interval")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Interval { get; init; }

        [JsonPropertyName("percentage")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int? Percentage { get; init; }

        [JsonPropertyName("statusCodeRanges")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public ImmutableArray<FailureStatusCodeRange>? StatusCodeRanges { get; init; }
    }

    public record FailureStatusCodeRange
    {
        [JsonPropertyName("max")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int? Max { get; init; }

        [JsonPropertyName("min")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int? Min { get; init; }
    }

    public record BackendCredentialsContract
    {
        [JsonPropertyName("authorization")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public BackendAuthorizationHeaderCredentials? Authorization { get; init; }

        [JsonPropertyName("certificate")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public ImmutableArray<string>? Certificate { get; init; }

        [JsonPropertyName("certificateIds")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public ImmutableArray<string>? CertificateIds { get; init; }

        [JsonPropertyName("header")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public JsonObject? Header { get; init; }

        [JsonPropertyName("query")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public JsonObject? Query { get; init; }
    }

    public record BackendAuthorizationHeaderCredentials
    {
        [JsonPropertyName("parameter")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Parameter { get; init; }

        [JsonPropertyName("scheme")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Scheme { get; init; }
    }

    public record BackendProperties
    {
        [JsonPropertyName("serviceFabricCluster")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public BackendServiceFabricClusterProperties? ServiceFabricCluster { get; init; }
    }

    public record BackendProxyContract
    {
        [JsonPropertyName("password")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Password { get; init; }

        [JsonPropertyName("url")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
#pragma warning disable CA1056 // URI-like properties should not be strings
        public string? Url { get; init; }

#pragma warning restore CA1056 // URI-like properties should not be strings
        [JsonPropertyName("username")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Username { get; init; }
    }

    public record BackendServiceFabricClusterProperties
    {
        [JsonPropertyName("clientCertificateId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? ClientCertificateId { get; init; }

        [JsonPropertyName("clientCertificatethumbprint")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? ClientCertificateThumbprint { get; init; }

        [JsonPropertyName("managementEndpoints")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public ImmutableArray<string>? ManagementEndpoints { get; init; }

        [JsonPropertyName("maxPartitionResolutionRetries")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int? MaxPartitionResolutionRetries { get; init; }

        [JsonPropertyName("serverCertificateThumbprints")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public ImmutableArray<string>? ServerCertificateThumbprints { get; init; }

        [JsonPropertyName("serverX509Names")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public ImmutableArray<X509CertificateName>? ServerX509Names { get; init; }
    }

    public record Pool
    {
        [JsonPropertyName("services")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public ImmutableArray<BackendPoolItem>? Services { get; init; }
    }

    public record BackendPoolItem
    {
        [JsonPropertyName("id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Id { get; init; }

        [JsonPropertyName("priority")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int? Priority { get; init; }

        [JsonPropertyName("weight")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int? Weight { get; init; }
    }

    public record BackendTlsProperties
    {
        [JsonPropertyName("validateCertificateChain")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool? ValidateCertificateChain { get; init; }

        [JsonPropertyName("validateCertificateName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool? ValidateCertificateName { get; init; }
    }

    public record X509CertificateName
    {
        [JsonPropertyName("issuerCertificateThumbprint")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? IssuerCertificateThumbprint { get; init; }

        [JsonPropertyName("name")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Name { get; init; }
    }
}

public static class BackendModule
{
    public static async ValueTask DeleteAll(this BackendsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await uri.ListNames(pipeline, cancellationToken)
                 .IterParallel(async name =>
                 {
                     var backendUri = new BackendUri { Parent = uri, Name = name };
                     await backendUri.Delete(pipeline, cancellationToken);
                 }, cancellationToken);

    public static IAsyncEnumerable<BackendName> ListNames(this BackendsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(BackendName.From);

    public static IAsyncEnumerable<(BackendName Name, BackendDto Dto)> List(this BackendsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        uri.ListNames(pipeline, cancellationToken)
           .SelectAwait(async name =>
           {
               var backendUri = new BackendUri { Parent = uri, Name = name };
               var dto = await backendUri.GetDto(pipeline, cancellationToken);
               return (name, dto);
           });

    public static async ValueTask<Option<BackendDto>> TryGetDto(this BackendUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var contentOption = await pipeline.GetContentOption(uri.ToUri(), cancellationToken);
        return contentOption.Map(content => content.ToObjectFromJson<BackendDto>());
    }

    public static async ValueTask<BackendDto> GetDto(this BackendUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = await pipeline.GetContent(uri.ToUri(), cancellationToken);
        return content.ToObjectFromJson<BackendDto>();
    }

    public static async ValueTask Delete(this BackendUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await pipeline.DeleteResource(uri.ToUri(), waitForCompletion: true, cancellationToken);

    public static async ValueTask PutDto(this BackendUri uri, BackendDto dto, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto);
        await pipeline.PutContent(uri.ToUri(), content, cancellationToken);
    }

    public static IEnumerable<BackendDirectory> ListDirectories(ManagementServiceDirectory serviceDirectory)
    {
        var backendsDirectory = BackendsDirectory.From(serviceDirectory);

        return from backendsDirectoryInfo in backendsDirectory.ToDirectoryInfo().ListDirectories("*")
               let name = BackendName.From(backendsDirectoryInfo.Name)
               select new BackendDirectory
               {
                   Parent = backendsDirectory,
                   Name = name
               };
    }

    public static IEnumerable<BackendInformationFile> ListInformationFiles(ManagementServiceDirectory serviceDirectory) =>
        from backendDirectory in ListDirectories(serviceDirectory)
        let informationFile = new BackendInformationFile { Parent = backendDirectory }
        where informationFile.ToFileInfo().Exists()
        select informationFile;

    public static async ValueTask WriteDto(this BackendInformationFile file, BackendDto dto, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto, JsonObjectExtensions.SerializerOptions);
        await file.ToFileInfo().OverwriteWithBinaryData(content, cancellationToken);
    }

    public static async ValueTask<BackendDto> ReadDto(this BackendInformationFile file, CancellationToken cancellationToken)
    {
        var content = await file.ToFileInfo().ReadAsBinaryData(cancellationToken);
        return content.ToObjectFromJson<BackendDto>();
    }
}