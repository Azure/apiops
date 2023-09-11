using Flurl;
using System;

namespace common;

public sealed record ProductPoliciesUri : IArtifactUri
{
    public Uri Uri { get; }

    public ProductPoliciesUri(ProductUri productUri)
    {
        Uri = productUri.AppendPath("policies");
    }
}

public sealed record ProductPolicyName
{
    private readonly string value;

    public ProductPolicyName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Product policy name cannot be null or whitespace.", nameof(value));
        }

        this.value = value;
    }

    public override string ToString() => value;
}

public sealed record ProductPolicyUri : IArtifactUri
{
    public Uri Uri { get; }

    public ProductPolicyUri(ProductPolicyName policyName, ProductPoliciesUri productPoliciesUri)
    {
        Uri = productPoliciesUri.AppendPath(policyName.ToString())
                                .SetQueryParam("format", "rawxml")
                                .ToUri();
    }
}

public sealed record ProductPolicyFile : IArtifactFile
{
    public ArtifactPath Path { get; }

    public ProductDirectory ProductDirectory { get; }

    public ProductPolicyFile(ProductPolicyName policyName, ProductDirectory productDirectory)
    {
        Path = productDirectory.Path.Append($"{policyName}.xml");
        ProductDirectory = productDirectory;
    }
}