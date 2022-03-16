using System;
using System.IO;
using System.Text.Json.Nodes;

namespace common;

public sealed record ApiOperationPolicyFile : FileRecord
{
    private static readonly string name = "policy.xml";

    public ApiOperationDirectory ApiOperationDirectory { get; }

    private ApiOperationPolicyFile(ApiOperationDirectory apiOperationDirectory) : base(apiOperationDirectory.Path.Append(name))
    {
        ApiOperationDirectory = apiOperationDirectory;
    }

    public static ApiOperationPolicyFile From(ApiOperationDirectory apiOperationDirectory) => new(apiOperationDirectory);

    public static ApiOperationPolicyFile? TryFrom(ServiceDirectory serviceDirectory, FileInfo file)
    {
        if (name.Equals(file.Name))
        {
            var apiOperationDirectory = ApiOperationDirectory.TryFrom(serviceDirectory, file.Directory);

            return apiOperationDirectory is null ? null : new(apiOperationDirectory);
        }
        else
        {
            return null;
        }
    }
}

public sealed record ApiOperationPolicyUri : UriRecord
{
    public ApiOperationPolicyUri(Uri value) : base(value)
    {
    }

    public static ApiOperationPolicyUri From(ApiOperationUri apiOperationUri) =>
        new(UriExtensions.AppendPath(apiOperationUri, "policies").AppendPath("policy").SetQueryParameter("format", "rawxml"));
}

public record ApiOperationPolicy
{
    public static string GetFromJson(JsonObject jsonObject) => jsonObject.GetJsonObjectProperty("properties").GetStringProperty("value");
}