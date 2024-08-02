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

public sealed record PolicyFragmentName : ResourceName, IResourceName<PolicyFragmentName>
{
    private PolicyFragmentName(string value) : base(value) { }

    public static PolicyFragmentName From(string value) => new(value);
}

public sealed record PolicyFragmentsUri : ResourceUri
{
    public required ManagementServiceUri ServiceUri { get; init; }

    private static string PathSegment { get; } = "policyFragments";

    protected override Uri Value => ServiceUri.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static PolicyFragmentsUri From(ManagementServiceUri serviceUri) =>
        new() { ServiceUri = serviceUri };
}

public sealed record PolicyFragmentUri : ResourceUri
{
    public required PolicyFragmentsUri Parent { get; init; }
    public required PolicyFragmentName Name { get; init; }

    protected override Uri Value => Parent.ToUri().AppendPathSegment(Name.ToString()).ToUri();

    public static PolicyFragmentUri From(PolicyFragmentName name, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = PolicyFragmentsUri.From(serviceUri),
            Name = name
        };
}

public sealed record PolicyFragmentsDirectory : ResourceDirectory
{
    public required ManagementServiceDirectory ServiceDirectory { get; init; }

    private static string Name { get; } = "policy fragments";

    protected override DirectoryInfo Value =>
        ServiceDirectory.ToDirectoryInfo().GetChildDirectory(Name);

    public static PolicyFragmentsDirectory From(ManagementServiceDirectory serviceDirectory) =>
        new() { ServiceDirectory = serviceDirectory };

    public static Option<PolicyFragmentsDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        directory is not null &&
        directory.Name == Name &&
        directory.Parent?.FullName == serviceDirectory.ToDirectoryInfo().FullName
            ? new PolicyFragmentsDirectory { ServiceDirectory = serviceDirectory }
            : Option<PolicyFragmentsDirectory>.None;
}

public sealed record PolicyFragmentDirectory : ResourceDirectory
{
    public required PolicyFragmentsDirectory Parent { get; init; }

    public required PolicyFragmentName Name { get; init; }

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name.ToString());

    public static PolicyFragmentDirectory From(PolicyFragmentName name, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = PolicyFragmentsDirectory.From(serviceDirectory),
            Name = name
        };

    public static Option<PolicyFragmentDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        from parent in PolicyFragmentsDirectory.TryParse(directory?.Parent, serviceDirectory)
        select new PolicyFragmentDirectory
        {
            Parent = parent,
            Name = PolicyFragmentName.From(directory!.Name)
        };
}

public sealed record PolicyFragmentInformationFile : ResourceFile
{
    public required PolicyFragmentDirectory Parent { get; init; }
    private static string Name { get; } = "policyFragmentInformation.json";

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile(Name);

    public static PolicyFragmentInformationFile From(PolicyFragmentName name, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = new PolicyFragmentDirectory
            {
                Parent = PolicyFragmentsDirectory.From(serviceDirectory),
                Name = name
            }
        };

    public static Option<PolicyFragmentInformationFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        file is not null && file.Name == Name
            ? from parent in PolicyFragmentDirectory.TryParse(file.Directory, serviceDirectory)
              select new PolicyFragmentInformationFile { Parent = parent }
            : Option<PolicyFragmentInformationFile>.None;
}

public sealed record PolicyFragmentPolicyFile : ResourceFile
{
    public required PolicyFragmentDirectory Parent { get; init; }
    private static string Name { get; } = "policy.xml";

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile(Name);

    public static PolicyFragmentPolicyFile From(PolicyFragmentName name, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = new PolicyFragmentDirectory
            {
                Parent = PolicyFragmentsDirectory.From(serviceDirectory),
                Name = name
            }
        };

    public static Option<PolicyFragmentPolicyFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        file is not null && file.Name == Name
            ? from parent in PolicyFragmentDirectory.TryParse(file.Directory, serviceDirectory)
              select new PolicyFragmentPolicyFile { Parent = parent }
            : Option<PolicyFragmentPolicyFile>.None;
}

public sealed record PolicyFragmentDto
{
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public required PolicyFragmentContract Properties { get; init; }

    public sealed record PolicyFragmentContract
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

public static class PolicyFragmentModule
{
    public static async ValueTask DeleteAll(this PolicyFragmentsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await uri.ListNames(pipeline, cancellationToken)
                 .IterParallel(async name => await PolicyFragmentUri.From(name, uri.ServiceUri)
                                                                .Delete(pipeline, cancellationToken),
                               cancellationToken);

    public static IAsyncEnumerable<PolicyFragmentName> ListNames(this PolicyFragmentsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(PolicyFragmentName.From);

    public static IAsyncEnumerable<(PolicyFragmentName Name, PolicyFragmentDto Dto)> List(this PolicyFragmentsUri policyFragmentsUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        policyFragmentsUri.ListNames(pipeline, cancellationToken)
                      .SelectAwait(async name =>
                      {
                          var uri = new PolicyFragmentUri { Parent = policyFragmentsUri, Name = name };
                          var dto = await uri.GetDto(pipeline, cancellationToken);
                          return (name, dto);
                      });

    public static async ValueTask<Option<PolicyFragmentDto>> TryGetDto(this PolicyFragmentUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var contentUri = uri.ToUri().AppendQueryParam("format", "rawxml").ToUri();
        var contentOption = await pipeline.GetContentOption(contentUri, cancellationToken);
        return contentOption.Map(content => content.ToObjectFromJson<PolicyFragmentDto>());
    }

    public static async ValueTask<PolicyFragmentDto> GetDto(this PolicyFragmentUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var contentUri = uri.ToUri().AppendQueryParam("format", "rawxml").ToUri();
        var content = await pipeline.GetContent(contentUri, cancellationToken);
        return content.ToObjectFromJson<PolicyFragmentDto>();
    }

    public static async ValueTask Delete(this PolicyFragmentUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await pipeline.DeleteResource(uri.ToUri(), waitForCompletion: true, cancellationToken);

    public static async ValueTask PutDto(this PolicyFragmentUri uri, PolicyFragmentDto dto, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto);
        await pipeline.PutContent(uri.ToUri(), content, cancellationToken);
    }

    public static IEnumerable<PolicyFragmentDirectory> ListDirectories(ManagementServiceDirectory serviceDirectory)
    {
        var policyFragmentsDirectory = PolicyFragmentsDirectory.From(serviceDirectory);

        return policyFragmentsDirectory.ToDirectoryInfo()
                                       .ListDirectories("*")
                                       .Select(directoryInfo => PolicyFragmentName.From(directoryInfo.Name))
                                       .Select(name => new PolicyFragmentDirectory { Parent = policyFragmentsDirectory, Name = name });
    }

    public static IEnumerable<PolicyFragmentInformationFile> ListInformationFiles(ManagementServiceDirectory serviceDirectory) =>
        ListDirectories(serviceDirectory)
            .Select(directory => new PolicyFragmentInformationFile { Parent = directory })
            .Where(informationFile => informationFile.ToFileInfo().Exists());

    public static IEnumerable<PolicyFragmentPolicyFile> ListPolicyFiles(ManagementServiceDirectory serviceDirectory) =>
        ListDirectories(serviceDirectory)
            .Select(directory => new PolicyFragmentPolicyFile { Parent = directory })
            .Where(informationFile => informationFile.ToFileInfo().Exists());

    public static async ValueTask WriteDto(this PolicyFragmentInformationFile file, PolicyFragmentDto dto, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto, JsonObjectExtensions.SerializerOptions);
        await file.ToFileInfo().OverwriteWithBinaryData(content, cancellationToken);
    }

    public static async ValueTask<PolicyFragmentDto> ReadDto(this PolicyFragmentInformationFile file, CancellationToken cancellationToken)
    {
        var content = await file.ToFileInfo().ReadAsBinaryData(cancellationToken);
        return content.ToObjectFromJson<PolicyFragmentDto>();
    }

    public static async ValueTask WritePolicy(this PolicyFragmentPolicyFile file, string policy, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromString(policy);
        await file.ToFileInfo().OverwriteWithBinaryData(content, cancellationToken);
    }
}