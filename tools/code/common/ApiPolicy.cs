using Flurl;
using System;

namespace common;

public sealed record ApiPoliciesUri : IArtifactUri
{
    public Uri Uri { get; }

    public ApiPoliciesUri(ApiUri apiUri)
    {
        Uri = apiUri.AppendPath("policies");
    }
}

public sealed record ApiPolicyName
{
    private readonly string value;

    public ApiPolicyName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"API policy name cannot be null or whitespace.", nameof(value));
        }

        this.value = value;
    }

    public override string ToString() => value;
}

public sealed record ApiPolicyUri : IArtifactUri
{
    public Uri Uri { get; }

    public ApiPolicyUri(ApiPolicyName policyName, ApiPoliciesUri apiPoliciesUri)
    {
        Uri = apiPoliciesUri.AppendPath(policyName.ToString())
                            .SetQueryParam("format", "rawxml")
                            .ToUri();
    }
}

public sealed record ApiPolicyFile : IArtifactFile
{
    public ArtifactPath Path { get; }

    public ApiDirectory ApiDirectory { get; }

    public ApiPolicyFile(ApiPolicyName policyName, ApiDirectory apiDirectory)
    {
        Path = apiDirectory.Path.Append($"{policyName}.xml");
        ApiDirectory = apiDirectory;
    }
}