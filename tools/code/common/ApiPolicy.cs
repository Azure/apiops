using Azure.Core.Pipeline;
using Flurl;
using LanguageExt;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public sealed record ApiPolicyName : ResourceName
{
    private ApiPolicyName(string value) : base(value) { }

    public static ApiPolicyName From(string value) => new(value);
}

public sealed record ApiPoliciesUri : ResourceUri
{
    public required ApiUri Parent { get; init; }

    private static string PathSegment { get; } = "policies";

    protected override Uri Value => Parent.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static ApiPoliciesUri From(ApiName name, ManagementServiceUri serviceUri) =>
        new() { Parent = ApiUri.From(name, serviceUri) };
}

public sealed record ApiPolicyUri : ResourceUri
{
    public required ApiPoliciesUri Parent { get; init; }
    public required ApiPolicyName Name { get; init; }

    protected override Uri Value => Parent.ToUri().AppendPathSegment(Name.ToString()).ToUri();

    public static ApiPolicyUri From(ApiPolicyName name, ApiName apiName, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = ApiPoliciesUri.From(apiName, serviceUri),
            Name = name
        };
}

public sealed record ApiPolicyFile : ResourceFile
{
    public required ApiDirectory Parent { get; init; }
    public required ApiPolicyName Name { get; init; }

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile($"{Name}.xml");

    public static ApiPolicyFile From(ApiPolicyName name, ApiName apiName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = ApiDirectory.From(apiName, serviceDirectory),
            Name = name
        };

    public static Option<ApiPolicyFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        from name in TryParseApiPolicyName(file)
        from parent in ApiDirectory.TryParse(file?.Directory, serviceDirectory)
        select new ApiPolicyFile
        {
            Name = name,
            Parent = parent
        };

    internal static Option<ApiPolicyName> TryParseApiPolicyName(FileInfo? file) =>
        file?.Name.EndsWith(".xml", StringComparison.Ordinal) switch
        {
            true => ApiPolicyName.From(Path.GetFileNameWithoutExtension(file.Name)),
            _ => Option<ApiPolicyName>.None
        };
}

public sealed record ApiPolicyDto
{
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public required ApiPolicyContract Properties { get; init; }

    public sealed record ApiPolicyContract
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

public static class ApiPolicyModule
{
    public static IAsyncEnumerable<ApiPolicyName> ListNames(this ApiPoliciesUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(ApiPolicyName.From);

    public static IAsyncEnumerable<(ApiPolicyName Name, ApiPolicyDto Dto)> List(this ApiPoliciesUri apiPoliciesUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        apiPoliciesUri.ListNames(pipeline, cancellationToken)
                          .SelectAwait(async name =>
                          {
                              var uri = new ApiPolicyUri { Parent = apiPoliciesUri, Name = name };
                              var dto = await uri.GetDto(pipeline, cancellationToken);
                              return (name, dto);
                          });

    public static async ValueTask<ApiPolicyDto> GetDto(this ApiPolicyUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var contentUri = uri.ToUri().AppendQueryParam("format", "rawxml").ToUri();
        var content = await pipeline.GetContent(contentUri, cancellationToken);
        return content.ToObjectFromJson<ApiPolicyDto>();
    }

    public static async ValueTask<Option<ApiPolicyDto>> TryGetDto(this ApiPolicyUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var option = await pipeline.GetContentOption(uri.ToUri(), cancellationToken);

        return option.Map(content => content.ToObjectFromJson<ApiPolicyDto>());
    }

    public static async ValueTask Delete(this ApiPolicyUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await pipeline.DeleteResource(uri.ToUri(), waitForCompletion: true, cancellationToken);

    public static async ValueTask PutDto(this ApiPolicyUri uri, ApiPolicyDto dto, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto);
        await pipeline.PutContent(uri.ToUri(), content, cancellationToken);
    }

    public static IEnumerable<ApiPolicyFile> ListPolicyFiles(ApiName apiName, ManagementServiceDirectory serviceDirectory)
    {
        var apiDirectory = ApiDirectory.From(apiName, serviceDirectory);

        return apiDirectory.ToDirectoryInfo()
                               .ListFiles("*")
                               .Choose(ApiPolicyFile.TryParseApiPolicyName)
                               .Select(name => new ApiPolicyFile { Name = name, Parent = apiDirectory });
    }

    public static async ValueTask WritePolicy(this ApiPolicyFile file, string policy, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromString(policy);
        await file.ToFileInfo().OverwriteWithBinaryData(content, cancellationToken);
    }

    public static async ValueTask<string> ReadPolicy(this ApiPolicyFile file, CancellationToken cancellationToken)
    {
        var content = await file.ToFileInfo().ReadAsBinaryData(cancellationToken);
        return content.ToString();
    }
}