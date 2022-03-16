using System;
using System.IO;
using System.Text.Json.Nodes;

namespace common;

public sealed record ApiPolicyFile : FileRecord
{
    private static readonly string name = "policy.xml";

    public ApiDirectory ApiDirectory { get; }

    private ApiPolicyFile(ApiDirectory apiDirectory) : base(apiDirectory.Path.Append(name))
    {
        ApiDirectory = apiDirectory;
    }

    public static ApiPolicyFile From(ApiDirectory apiDirectory) => new(apiDirectory);

    public static ApiPolicyFile? TryFrom(ServiceDirectory serviceDirectory, FileInfo file)
    {
        if (name.Equals(file.Name))
        {
            var apiDirectory = ApiDirectory.TryFrom(serviceDirectory, file.Directory);

            return apiDirectory is null ? null : new(apiDirectory);
        }
        else
        {
            return null;
        }
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