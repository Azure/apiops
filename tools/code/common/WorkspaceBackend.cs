﻿using Azure.Core.Pipeline;
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

public sealed record WorkspaceBackendName : ResourceName, IResourceName<WorkspaceBackendName>
{
    private WorkspaceBackendName(string value) : base(value) { }

    public static WorkspaceBackendName From(string value) => new(value);
}

public sealed record WorkspaceBackendsUri : ResourceUri
{
    public required WorkspaceUri Parent { get; init; }

    private static string PathSegment { get; } = "backends";

    protected override Uri Value =>
        Parent.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static WorkspaceBackendsUri From(WorkspaceName workspaceName, ManagementServiceUri serviceUri) =>
        new() { Parent = WorkspaceUri.From(workspaceName, serviceUri) };
}

public sealed record WorkspaceBackendUri : ResourceUri
{
    public required WorkspaceBackendsUri Parent { get; init; }

    public required WorkspaceBackendName Name { get; init; }

    protected override Uri Value =>
        Parent.ToUri().AppendPathSegment(Name.Value).ToUri();

    public static WorkspaceBackendUri From(WorkspaceBackendName workspaceBackendName, WorkspaceName workspaceName, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = WorkspaceBackendsUri.From(workspaceName, serviceUri),
            Name = workspaceBackendName
        };
}

public sealed record WorkspaceBackendsDirectory : ResourceDirectory
{
    public required WorkspaceDirectory Parent { get; init; }

    private static string Name { get; } = "backends";

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name);

    public static WorkspaceBackendsDirectory From(WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new() { Parent = WorkspaceDirectory.From(workspaceName, serviceDirectory) };

    public static Option<WorkspaceBackendsDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        directory?.Name == Name
            ? from parent in WorkspaceDirectory.TryParse(directory.Parent, serviceDirectory)
              select new WorkspaceBackendsDirectory { Parent = parent }
            : Option<WorkspaceBackendsDirectory>.None;
}

public sealed record WorkspaceBackendDirectory : ResourceDirectory
{
    public required WorkspaceBackendsDirectory Parent { get; init; }

    public required WorkspaceBackendName Name { get; init; }

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name.Value);

    public static WorkspaceBackendDirectory From(WorkspaceBackendName workspaceBackendName, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceBackendsDirectory.From(workspaceName, serviceDirectory),
            Name = workspaceBackendName
        };

    public static Option<WorkspaceBackendDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        directory is null
            ? Option<WorkspaceBackendDirectory>.None
            : from parent in WorkspaceBackendsDirectory.TryParse(directory.Parent, serviceDirectory)
              let name = WorkspaceBackendName.From(directory.Name)
              select new WorkspaceBackendDirectory
              {
                  Parent = parent,
                  Name = name
              };
}

public sealed record WorkspaceBackendInformationFile : ResourceFile
{
    public required WorkspaceBackendDirectory Parent { get; init; }

    private static string Name { get; } = "backendInformation.json";

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile(Name);

    public static WorkspaceBackendInformationFile From(WorkspaceBackendName workspaceBackendName, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceBackendDirectory.From(workspaceBackendName, workspaceName, serviceDirectory)
        };

    public static Option<WorkspaceBackendInformationFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        file?.Name == Name
            ? from parent in WorkspaceBackendDirectory.TryParse(file.Directory, serviceDirectory)
              select new WorkspaceBackendInformationFile
              {
                  Parent = parent
              }
            : Option<WorkspaceBackendInformationFile>.None;
}

public sealed record WorkspaceBackendDto
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

public static class WorkspaceBackendModule
{
    public static IAsyncEnumerable<WorkspaceBackendName> ListNames(this WorkspaceBackendsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(WorkspaceBackendName.From);

    public static IAsyncEnumerable<(WorkspaceBackendName Name, WorkspaceBackendDto Dto)> List(this WorkspaceBackendsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        uri.ListNames(pipeline, cancellationToken)
           .SelectAwait(async name =>
           {
               var resourceUri = new WorkspaceBackendUri { Parent = uri, Name = name };
               var dto = await resourceUri.GetDto(pipeline, cancellationToken);
               return (name, dto);
           });

    public static async ValueTask<WorkspaceBackendDto> GetDto(this WorkspaceBackendUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = await pipeline.GetContent(uri.ToUri(), cancellationToken);
        return content.ToObjectFromJson<WorkspaceBackendDto>();
    }

    public static async ValueTask Delete(this WorkspaceBackendUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await pipeline.DeleteResource(uri.ToUri(), waitForCompletion: true, cancellationToken);

    public static async ValueTask PutDto(this WorkspaceBackendUri uri, WorkspaceBackendDto dto, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto);
        await pipeline.PutContent(uri.ToUri(), content, cancellationToken);
    }

    public static IEnumerable<WorkspaceBackendDirectory> ListDirectories(ManagementServiceDirectory serviceDirectory) =>
        from workspaceDirectory in WorkspaceModule.ListDirectories(serviceDirectory)
        let workspaceBackendsDirectory = new WorkspaceBackendsDirectory { Parent = workspaceDirectory }
        where workspaceBackendsDirectory.ToDirectoryInfo().Exists()
        from workspaceBackendDirectoryInfo in workspaceBackendsDirectory.ToDirectoryInfo().ListDirectories("*")
        let name = WorkspaceBackendName.From(workspaceBackendDirectoryInfo.Name)
        select new WorkspaceBackendDirectory
        {
            Parent = workspaceBackendsDirectory,
            Name = name
        };

    public static IEnumerable<WorkspaceBackendInformationFile> ListInformationFiles(ManagementServiceDirectory serviceDirectory) =>
        from workspaceBackendDirectory in ListDirectories(serviceDirectory)
        let informationFile = new WorkspaceBackendInformationFile { Parent = workspaceBackendDirectory }
        where informationFile.ToFileInfo().Exists()
        select informationFile;

    public static async ValueTask WriteDto(this WorkspaceBackendInformationFile file, WorkspaceBackendDto dto, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto, JsonObjectExtensions.SerializerOptions);
        await file.ToFileInfo().OverwriteWithBinaryData(content, cancellationToken);
    }

    public static async ValueTask<WorkspaceBackendDto> ReadDto(this WorkspaceBackendInformationFile file, CancellationToken cancellationToken)
    {
        var content = await file.ToFileInfo().ReadAsBinaryData(cancellationToken);
        return content.ToObjectFromJson<WorkspaceBackendDto>();
    }
}