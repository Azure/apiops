using Flurl;
using System;

namespace common;

public sealed record ServicePoliciesUri : IArtifactUri
{
    public Uri Uri { get; }

    public ServicePoliciesUri(ServiceUri serviceUri)
    {
        Uri = serviceUri.AppendPath("policies");
    }
}

public sealed record ServicePolicyName
{
    private readonly string value;

    public ServicePolicyName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Service policy name cannot be null or whitespace.", nameof(value));
        }

        this.value = value;
    }

    public override string ToString() => value;
}

public sealed record ServicePolicyUri : IArtifactUri
{
    public Uri Uri { get; }

    public ServicePolicyUri(ServicePolicyName policyName, ServicePoliciesUri servicePoliciesUri)
    {
        Uri = servicePoliciesUri.AppendPath(policyName.ToString())
                                .SetQueryParam("format", "rawxml")
                                .ToUri();
    }
}

public sealed record ServicePolicyFile : IArtifactFile
{
    public ArtifactPath Path { get; }

    public ServiceDirectory ServiceDirectory { get; }

    public string PolicyName => this.GetNameWithoutExtensions();

    public ServicePolicyFile(ServicePolicyName policyName, ServiceDirectory serviceDirectory)
    {
        Path = serviceDirectory.Path.Append($"{policyName}.xml");
        ServiceDirectory = serviceDirectory;
    }
}