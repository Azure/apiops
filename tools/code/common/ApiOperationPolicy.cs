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

public sealed record ApiOperationPolicyName : ResourceName
{
    private ApiOperationPolicyName(string value) : base(value) { }

    public static ApiOperationPolicyName From(string value) => new(value);
}

public sealed record ApiOperationPoliciesUri : ResourceUri
{
    public required ApiOperationUri Parent { get; init; }

    private static string PathSegment { get; } = "policies";

    protected override Uri Value => Parent.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static ApiOperationPoliciesUri From(ApiOperationName name, ApiName apiName, ManagementServiceUri serviceUri) =>
        new() { Parent = ApiOperationUri.From(name, apiName, serviceUri) };
}

public sealed record ApiOperationPolicyUri : ResourceUri
{
    public required ApiOperationPoliciesUri Parent { get; init; }
    public required ApiOperationPolicyName Name { get; init; }

    protected override Uri Value => Parent.ToUri().AppendPathSegment(Name.ToString()).ToUri();

    public static ApiOperationPolicyUri From(ApiOperationPolicyName name, ApiOperationName apioperationName, ApiName apiName, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = ApiOperationPoliciesUri.From(apioperationName, apiName, serviceUri),
            Name = name
        };
}

public sealed record ApiOperationPolicyFile : ResourceFile
{
    public required ApiOperationDirectory Parent { get; init; }
    public required ApiOperationPolicyName Name { get; init; }

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile($"{Name}.xml");

    public static ApiOperationPolicyFile From(ApiOperationPolicyName name, ApiOperationName apioperationName, ApiName apiName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = ApiOperationDirectory.From(apioperationName, apiName, serviceDirectory),
            Name = name
        };

    public static Option<ApiOperationPolicyFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        from name in TryParseApiOperationPolicyName(file)
        from parent in ApiOperationDirectory.TryParse(file?.Directory, serviceDirectory)
        select new ApiOperationPolicyFile
        {
            Name = name,
            Parent = parent
        };

    internal static Option<ApiOperationPolicyName> TryParseApiOperationPolicyName(FileInfo? file) =>
        file?.Name.EndsWith(".xml", StringComparison.Ordinal) switch
        {
            true => ApiOperationPolicyName.From(Path.GetFileNameWithoutExtension(file.Name)),
            _ => Option<ApiOperationPolicyName>.None
        };
}

public sealed record ApiOperationPolicyDto
{
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public required ApiOperationPolicyContract Properties { get; init; }

    public sealed record ApiOperationPolicyContract
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

public static class ApiOperationPolicyModule
{
    public static IAsyncEnumerable<ApiOperationPolicyName> ListNames(this ApiOperationPoliciesUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(ApiOperationPolicyName.From);

    public static IAsyncEnumerable<(ApiOperationPolicyName Name, ApiOperationPolicyDto Dto)> List(this ApiOperationPoliciesUri apioperationPoliciesUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        apioperationPoliciesUri.ListNames(pipeline, cancellationToken)
                          .SelectAwait(async name =>
                          {
                              var uri = new ApiOperationPolicyUri { Parent = apioperationPoliciesUri, Name = name };
                              var dto = await uri.GetDto(pipeline, cancellationToken);
                              return (name, dto);
                          });

    public static async ValueTask<ApiOperationPolicyDto> GetDto(this ApiOperationPolicyUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var contentUri = uri.ToUri().AppendQueryParam("format", "rawxml").ToUri();
        var content = await pipeline.GetContent(contentUri, cancellationToken);
        return content.ToObjectFromJson<ApiOperationPolicyDto>();
    }

    public static async ValueTask Delete(this ApiOperationPolicyUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await pipeline.DeleteResource(uri.ToUri(), waitForCompletion: true, cancellationToken);

    public static async ValueTask PutDto(this ApiOperationPolicyUri uri, ApiOperationPolicyDto dto, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto);
        await pipeline.PutContent(uri.ToUri(), content, cancellationToken);
    }

    public static async ValueTask WritePolicy(this ApiOperationPolicyFile file, string policy, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromString(policy);
        await file.ToFileInfo().OverwriteWithBinaryData(content, cancellationToken);
    }

    public static async ValueTask<string> ReadPolicy(this ApiOperationPolicyFile file, CancellationToken cancellationToken)
    {
        var content = await file.ToFileInfo().ReadAsBinaryData(cancellationToken);
        return content.ToString();
    }
}