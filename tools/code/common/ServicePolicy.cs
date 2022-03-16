using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public sealed record ServicePolicyFile : FileRecord
{
    private static readonly string name = "policy.xml";

    public ServiceDirectory ServiceDirectory { get; }

    private ServicePolicyFile(ServiceDirectory serviceDirectory) : base(serviceDirectory.Path.Append(name))
    {
        ServiceDirectory = serviceDirectory;
    }

    public static ServicePolicyFile From(ServiceDirectory serviceDirectory) => new(serviceDirectory);

    public static ServicePolicyFile? TryFrom(ServiceDirectory serviceDirectory, FileInfo? file) =>
        name.Equals(file?.Name) && serviceDirectory.PathEquals(file.Directory)
        ? new(serviceDirectory)
        : null;
}

public sealed record ServicePolicyUri : UriRecord
{
    public ServicePolicyUri(Uri value) : base(value)
    {
    }

    public static ServicePolicyUri From(ServiceUri serviceUri) =>
        new(UriExtensions.AppendPath(serviceUri, "policies").AppendPath("policy").SetQueryParameter("format", "rawxml"));
}

public record ServicePolicy
{
    public static string GetFromJson(JsonObject jsonObject) => jsonObject.GetJsonObjectProperty("properties").GetStringProperty("value");
}