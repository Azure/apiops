using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public sealed record ApiPolicyFile : FileRecord
{
    private static readonly string name = "policy.xml";
    private readonly ApisDirectory apisDirectory;
    private readonly ApiDisplayName apiDisplayName;

    private ApiPolicyFile(ApisDirectory apisDirectory, ApiDisplayName apiDisplayName)
        : base(apisDirectory.Path.Append(apiDisplayName).Append(name))
    {
        this.apisDirectory = apisDirectory;
        this.apiDisplayName = apiDisplayName;
    }

    public async Task<JsonObject> ToJsonObject(CancellationToken cancellationToken)
    {
        var policyText = await File.ReadAllTextAsync(Path, cancellationToken);
        var propertiesJson = new JsonObject().AddProperty("format", "rawxml")
                                             .AddProperty("value", policyText);

        return new JsonObject().AddProperty("properties", propertiesJson);
    }

    public ApiInformationFile GetApiInformationFile() => ApiInformationFile.From(apisDirectory, apiDisplayName);

    public static ApiPolicyFile From(ApisDirectory apisDirectory, ApiDisplayName displayName)
        => new(apisDirectory, displayName);

    public static ApiPolicyFile? TryFrom(ServiceDirectory serviceDirectory, FileInfo file)
    {
        if (name.Equals(file.Name) is false)
        {
            return null;
        }

        var directory = file.Directory;
        if (directory is null)
        {
            return null;
        }

        var apisDirectory = ApisDirectory.TryFrom(serviceDirectory, directory.Parent);
        return apisDirectory is null
            ? null
            : new(apisDirectory, ApiDisplayName.From(directory.Name));
    }
}

public sealed record ApiPolicyUri : UriRecord
{
    public ApiPolicyUri(Uri value) : base(value)
    {
    }

    public static ApiPolicyUri From(ApiUri apiUri) =>
        new(UriExtensions.AppendPath(apiUri, "policies").AppendPath("policy").SetQueryParameter("format", "rawxml"));
}

public record ApiPolicy
{
    public static string GetFromJson(JsonObject jsonObject) => jsonObject.GetJsonObjectProperty("properties").GetStringProperty("value");
}