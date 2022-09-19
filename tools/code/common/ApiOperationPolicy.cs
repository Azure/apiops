using Flurl;
using System;

namespace common;

public sealed record ApiOperationPoliciesUri : IArtifactUri
{
    public Uri Uri { get; }

    public ApiOperationPoliciesUri(ApiOperationUri apiOperationUri)
    {
        Uri = apiOperationUri.AppendPath("policies");
    }
}

public sealed record ApiOperationPolicyName
{
    private readonly string value;

    public ApiOperationPolicyName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"API operation policy name cannot be null or whitespace.", nameof(value));
        }

        this.value = value;
    }

    public override string ToString() => value;
}

public sealed record ApiOperationPolicyUri : IArtifactUri
{
    public Uri Uri { get; }

    public ApiOperationPolicyUri(ApiOperationPolicyName policyName, ApiOperationPoliciesUri apiOperationPoliciesUri)
    {
        Uri = apiOperationPoliciesUri.AppendPath(policyName.ToString())
                                     .SetQueryParam("format", "rawxml")
                                     .ToUri();
    }
}

public sealed record ApiOperationPolicyFile : IArtifactFile
{
    public ArtifactPath Path { get; }

    public ApiOperationDirectory ApiOperationDirectory { get; }

    public ApiOperationPolicyFile(ApiOperationPolicyName policyName, ApiOperationDirectory apiOperationDirectory)
    {
        Path = apiOperationDirectory.Path.Append($"{policyName}.xml");
        ApiOperationDirectory = apiOperationDirectory;
    }
}