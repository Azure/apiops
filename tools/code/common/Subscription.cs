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

public sealed record SubscriptionName : ResourceName, IResourceName<SubscriptionName>
{
    private SubscriptionName(string value) : base(value) { }

    public static SubscriptionName From(string value) => new(value);
}

public sealed record SubscriptionsUri : ResourceUri
{
    public required ManagementServiceUri ServiceUri { get; init; }

    private static string PathSegment { get; } = "subscriptions";

    protected override Uri Value => ServiceUri.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static SubscriptionsUri From(ManagementServiceUri serviceUri) =>
        new() { ServiceUri = serviceUri };
}

public sealed record SubscriptionUri : ResourceUri
{
    public required SubscriptionsUri Parent { get; init; }
    public required SubscriptionName Name { get; init; }

    protected override Uri Value => Parent.ToUri().AppendPathSegment(Name.ToString()).ToUri();

    public static SubscriptionUri From(SubscriptionName name, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = SubscriptionsUri.From(serviceUri),
            Name = name
        };
}

public sealed record SubscriptionsDirectory : ResourceDirectory
{
    public required ManagementServiceDirectory ServiceDirectory { get; init; }

    private static string Name { get; } = "subscriptions";

    protected override DirectoryInfo Value =>
        ServiceDirectory.ToDirectoryInfo().GetChildDirectory(Name);

    public static SubscriptionsDirectory From(ManagementServiceDirectory serviceDirectory) =>
        new() { ServiceDirectory = serviceDirectory };

    public static Option<SubscriptionsDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        directory is not null &&
        directory.Name == Name &&
        directory.Parent?.FullName == serviceDirectory.ToDirectoryInfo().FullName
            ? new SubscriptionsDirectory { ServiceDirectory = serviceDirectory }
            : Option<SubscriptionsDirectory>.None;
}

public sealed record SubscriptionDirectory : ResourceDirectory
{
    public required SubscriptionsDirectory Parent { get; init; }

    public required SubscriptionName Name { get; init; }

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name.Value);

    public static SubscriptionDirectory From(SubscriptionName name, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = SubscriptionsDirectory.From(serviceDirectory),
            Name = name
        };

    public static Option<SubscriptionDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        from parent in SubscriptionsDirectory.TryParse(directory?.Parent, serviceDirectory)
        select new SubscriptionDirectory
        {
            Parent = parent,
            Name = SubscriptionName.From(directory!.Name)
        };
}

public sealed record SubscriptionInformationFile : ResourceFile
{
    public required SubscriptionDirectory Parent { get; init; }

    public static string Name { get; } = "subscriptionInformation.json";

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile(Name);

    public static SubscriptionInformationFile From(SubscriptionName name, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = SubscriptionDirectory.From(name, serviceDirectory)
        };

    public static Option<SubscriptionInformationFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        file is not null &&
        file.Name == Name
            ? from parent in SubscriptionDirectory.TryParse(file.Directory, serviceDirectory)
              select From(parent.Name, serviceDirectory)
            : Option<SubscriptionInformationFile>.None;
}

public sealed record SubscriptionDto
{
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public required SubscriptionContract Properties { get; init; }

    public sealed record SubscriptionContract
    {
        [JsonPropertyName("displayName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? DisplayName { get; init; }

        [JsonPropertyName("scope")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Scope { get; init; }

        [JsonPropertyName("allowTracing")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool? AllowTracing { get; init; }

        [JsonPropertyName("ownerId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? OwnerId { get; init; }

        [JsonPropertyName("primaryKey")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? PrimaryKey { get; init; }

        [JsonPropertyName("secondaryKey")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? SecondaryKey { get; init; }

        [JsonPropertyName("state")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? State { get; init; }
    }
}

public static class SubscriptionModule
{
    public static async ValueTask DeleteAll(this SubscriptionsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await uri.ListNames(pipeline, cancellationToken)
                 .IterParallel(async name =>
                 {
                     var resourceUri = SubscriptionUri.From(name, uri.ServiceUri);
                     await resourceUri.Delete(pipeline, cancellationToken);
                 }, cancellationToken);

    public static IAsyncEnumerable<SubscriptionName> ListNames(this SubscriptionsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(SubscriptionName.From);

    public static IAsyncEnumerable<(SubscriptionName Name, SubscriptionDto Dto)> List(this SubscriptionsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        uri.ListNames(pipeline, cancellationToken)
           .SelectAwait(async name =>
           {
               var resourceUri = new SubscriptionUri { Parent = uri, Name = name };
               var dto = await resourceUri.GetDto(pipeline, cancellationToken);
               return (name, dto);
           });

    public static async ValueTask<Option<SubscriptionDto>> TryGetDto(this SubscriptionUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var contentOption = await pipeline.GetContentOption(uri.ToUri(), cancellationToken);
        return contentOption.Map(content => content.ToObjectFromJson<SubscriptionDto>());
    }

    public static async ValueTask<SubscriptionDto> GetDto(this SubscriptionUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = await pipeline.GetContent(uri.ToUri(), cancellationToken);
        return content.ToObjectFromJson<SubscriptionDto>();
    }

    public static async ValueTask Delete(this SubscriptionUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await pipeline.DeleteResource(uri.ToUri(), waitForCompletion: true, cancellationToken);

    public static async ValueTask PutDto(this SubscriptionUri uri, SubscriptionDto dto, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto);
        await pipeline.PutContent(uri.ToUri(), content, cancellationToken);
    }

    public static IEnumerable<SubscriptionDirectory> ListDirectories(ManagementServiceDirectory serviceDirectory)
    {
        var subscriptionsDirectory = SubscriptionsDirectory.From(serviceDirectory);

        return subscriptionsDirectory.ToDirectoryInfo()
                                     .ListDirectories("*")
                                     .Select(directoryInfo => SubscriptionName.From(directoryInfo.Name))
                                     .Select(name => new SubscriptionDirectory { Parent = subscriptionsDirectory, Name = name });
    }

    public static IEnumerable<SubscriptionInformationFile> ListInformationFiles(ManagementServiceDirectory serviceDirectory) =>
        ListDirectories(serviceDirectory)
            .Select(directory => new SubscriptionInformationFile { Parent = directory })
            .Where(informationFile => informationFile.ToFileInfo().Exists());

    public static async ValueTask WriteDto(this SubscriptionInformationFile file, SubscriptionDto dto, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto, JsonObjectExtensions.SerializerOptions);
        await file.ToFileInfo().OverwriteWithBinaryData(content, cancellationToken);
    }

    public static async ValueTask<SubscriptionDto> ReadDto(this SubscriptionInformationFile file, CancellationToken cancellationToken)
    {
        var content = await file.ToFileInfo().ReadAsBinaryData(cancellationToken);
        return content.ToObjectFromJson<SubscriptionDto>();
    }

    public static Option<ApiName> TryGetApiName(SubscriptionDto dto) =>
        from scope in Prelude.Optional(dto.Properties.Scope)
        where scope.Contains("/apis/", StringComparison.OrdinalIgnoreCase)
        from apiNameString in scope.Split('/').LastOrNone()
        select ApiName.From(apiNameString);

    public static Option<ProductName> TryGetProductName(SubscriptionDto dto) =>
        from scope in Prelude.Optional(dto.Properties.Scope)
        where scope.Contains("/products/", StringComparison.OrdinalIgnoreCase)
        from productNameString in scope.Split('/').LastOrNone()
        select ProductName.From(productNameString);
}