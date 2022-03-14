using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public sealed record ServicePolicyFile : FileRecord
{
    private static readonly string name = "policy.xml";

    private ServicePolicyFile(RecordPath path) : base(path)
    {
    }

    public static ServicePolicyFile From(ServiceDirectory serviceDirectory)
    {
        var path = serviceDirectory.Path.Append(name);

        return new(path);
    }

    public async Task<JsonObject> ToJsonObject(CancellationToken cancellationToken)
    {
        var policyText = await File.ReadAllTextAsync(Path, cancellationToken);
        var propertiesJson = new JsonObject().AddProperty("format", "rawxml")
                                             .AddProperty("value", policyText);

        return new JsonObject().AddProperty("properties", propertiesJson);
    }

    public static ServicePolicyFile? TryFrom(ServiceDirectory serviceDirectory, FileInfo file) =>
        name.Equals(file.Name) && serviceDirectory.Path.PathEquals(file.Directory?.FullName)
        ? new(RecordPath.From(file.FullName))
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