using Azure.Core.Pipeline;
using Flurl;
using LanguageExt;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public sealed record ServicePolicyName : ResourceName
{
    private ServicePolicyName(string value) : base(value) { }

    public static ServicePolicyName From(string value) => new(value);
}

public sealed record ServicePoliciesUri : ResourceUri
{
    public required ManagementServiceUri ServiceUri { get; init; }

    private static string PathSegment { get; } = "policies";

    protected override Uri Value => ServiceUri.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static ServicePoliciesUri From(ManagementServiceUri serviceUri) =>
        new() { ServiceUri = serviceUri };
}

public sealed record ServicePolicyUri : ResourceUri
{
    public required ServicePoliciesUri Parent { get; init; }
    public required ServicePolicyName Name { get; init; }

    protected override Uri Value => Parent.ToUri().AppendPathSegment(Name.ToString()).ToUri();

    public static ServicePolicyUri From(ServicePolicyName name, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = ServicePoliciesUri.From(serviceUri),
            Name = name
        };
}

public sealed record ServicePolicyFile : ResourceFile
{
    public required ManagementServiceDirectory Parent { get; init; }
    public required ServicePolicyName Name { get; init; }

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile($"{Name}.xml");

    public static ServicePolicyFile From(ServicePolicyName name, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = serviceDirectory,
            Name = name
        };

    public static Option<ServicePolicyFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        file is not null
        && file.Name.EndsWith(".xml", StringComparison.Ordinal)
        && file.Directory?.FullName == serviceDirectory.ToDirectoryInfo().FullName
            ? new ServicePolicyFile
            {
                Parent = serviceDirectory,
                Name = ServicePolicyName.From(Path.GetFileNameWithoutExtension(file.Name))
            }
            : Option<ServicePolicyFile>.None;
}

public sealed record ServicePolicyDto
{
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public required ServicePolicyContract Properties { get; init; }

    public sealed record ServicePolicyContract
    {
        [JsonPropertyName("description")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Description { get; init; }

        [JsonPropertyName("format")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Format { get; init; }

        [JsonPropertyName("value")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Value { get; init; }
    }
}

public static class ServicePolicyModule
{
    public static async ValueTask DeleteAll(this ServicePoliciesUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await uri.ListNames(pipeline, cancellationToken)
                 .IterParallel(async name => await ServicePolicyUri.From(name, uri.ServiceUri)
                                                                .Delete(pipeline, cancellationToken),
                               cancellationToken);

    public static IAsyncEnumerable<ServicePolicyName> ListNames(this ServicePoliciesUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(ServicePolicyName.From);

    public static IAsyncEnumerable<(ServicePolicyName Name, ServicePolicyDto Dto)> List(this ServicePoliciesUri servicePoliciesUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        servicePoliciesUri.ListNames(pipeline, cancellationToken)
                      .SelectAwait(async name =>
                      {
                          var uri = new ServicePolicyUri { Parent = servicePoliciesUri, Name = name };
                          var dto = await uri.GetDto(pipeline, cancellationToken);
                          return (name, dto);
                      });

    public static async ValueTask<ServicePolicyDto> GetDto(this ServicePolicyUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var contentUri = uri.ToUri().AppendQueryParam("format", "rawxml").ToUri();
        var content = await pipeline.GetContent(contentUri, cancellationToken);
        return content.ToObjectFromJson<ServicePolicyDto>();
    }

    public static async ValueTask Delete(this ServicePolicyUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await pipeline.DeleteResource(uri.ToUri(), waitForCompletion: true, cancellationToken);

    public static async ValueTask PutDto(this ServicePolicyUri uri, ServicePolicyDto dto, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto);
        await pipeline.PutContent(uri.ToUri(), content, cancellationToken);
    }

    public static IEnumerable<ServicePolicyFile> ListPolicyFiles(ManagementServiceDirectory serviceDirectory) =>
        serviceDirectory.ToDirectoryInfo()
                        .ListFiles("*")
                        .Choose(file => ServicePolicyFile.TryParse(file, serviceDirectory));

    public static async ValueTask WritePolicy(this ServicePolicyFile file, string policy, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromString(policy);
        await file.ToFileInfo().OverwriteWithBinaryData(content, cancellationToken);
    }

    public static async ValueTask<string> ReadPolicy(this ServicePolicyFile file, CancellationToken cancellationToken)
    {
        var content = await file.ToFileInfo().ReadAsBinaryData(cancellationToken);
        return content.ToString();
    }
}