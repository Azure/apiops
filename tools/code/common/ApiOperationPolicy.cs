using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public sealed record ApiOperationPolicyFile : FileRecord
{
    private static readonly string name = "policy.xml";
    private readonly ApiOperationsDirectory apiOperationsDirectory;
    private readonly ApiOperationDisplayName apiOperationDisplayName;

    private ApiOperationPolicyFile(ApiOperationsDirectory apiOperationsDirectory, ApiOperationDisplayName apiOperationDisplayName)
        : base(apiOperationsDirectory.Path.Append(apiOperationDisplayName).Append(name))
    {
        this.apiOperationsDirectory = apiOperationsDirectory;
        this.apiOperationDisplayName = apiOperationDisplayName;
    }

    public async Task<JsonObject> ToJsonObject(CancellationToken cancellationToken)
    {
        var policyText = await File.ReadAllTextAsync(Path, cancellationToken);
        var propertiesJson = new JsonObject().AddProperty("format", "rawxml")
                                             .AddProperty("value", policyText);

        return new JsonObject().AddProperty("properties", propertiesJson);
    }

    //public ApiOperationInformationFile GetApiOperationInformationFile() => ApiOperationInformationFile.From(apiOperationsDirectory, apiOperationDisplayName);

    public static ApiOperationPolicyFile From(ApiOperationsDirectory apiOperationsDirectory, ApiOperationDisplayName displayName)
        => new(apiOperationsDirectory, displayName);

    public static ApiOperationPolicyFile? TryFrom(ServiceDirectory serviceDirectory, FileInfo file)
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

        var apiOperationsDirectory = ApiOperationsDirectory.TryFrom(serviceDirectory, directory.Parent);
        return apiOperationsDirectory is null
            ? null
            : new(apiOperationsDirectory, ApiOperationDisplayName.From(directory.Name));
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