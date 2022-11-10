using Flurl;
using System;
using System.Text.Json.Nodes;

namespace common;

public sealed record PolicyFragmentsUri : IArtifactUri
{
    public Uri Uri { get; }

    public PolicyFragmentsUri(ServiceUri serviceUri)
    {
        Uri = serviceUri.AppendPath("policyFragments");
    }
}

public sealed record PolicyFragmentsDirectory : IArtifactDirectory
{
    public static string Name { get; } = "policy fragments";

    public ArtifactPath Path { get; }

    public ServiceDirectory ServiceDirectory { get; }

    public PolicyFragmentsDirectory(ServiceDirectory serviceDirectory)
    {
        Path = serviceDirectory.Path.Append(Name);
        ServiceDirectory = serviceDirectory;
    }
}

public sealed record PolicyFragmentName
{
    private readonly string value;

    public PolicyFragmentName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Policy fragment name cannot be null or whitespace.", nameof(value));
        }

        this.value = value;
    }

    public override string ToString() => value;
}

public sealed record PolicyFragmentUri : IArtifactUri
{
    public Uri Uri { get; }

    public PolicyFragmentUri(PolicyFragmentName policyFragmentName, PolicyFragmentsUri policyFragmentsUri)
    {
        Uri = policyFragmentsUri.AppendPath(policyFragmentName.ToString())
                                .SetQueryParam("format", "rawxml")
                                .ToUri();
    }
}

public sealed record PolicyFragmentDirectory : IArtifactDirectory
{
    public ArtifactPath Path { get; }

    public PolicyFragmentsDirectory PolicyFragmentsDirectory { get; }

    public PolicyFragmentDirectory(PolicyFragmentName policyFragmentName, PolicyFragmentsDirectory policyFragmentsDirectory)
    {
        Path = policyFragmentsDirectory.Path.Append(policyFragmentName.ToString());
        PolicyFragmentsDirectory = policyFragmentsDirectory;
    }
}

public sealed record PolicyFragmentInformationFile : IArtifactFile
{
    public static string Name { get; } = "policyFragmentInformation.json";

    public ArtifactPath Path { get; }

    public PolicyFragmentDirectory PolicyFragmentDirectory { get; }

    public PolicyFragmentInformationFile(PolicyFragmentDirectory policyFragmentDirectory)
    {
        Path = policyFragmentDirectory.Path.Append(Name);
        PolicyFragmentDirectory = policyFragmentDirectory;
    }
}

public sealed record PolicyFragmentPolicyFile : IArtifactFile
{
    public static string Name { get; } = "policy.xml";

    public ArtifactPath Path { get; }

    public PolicyFragmentDirectory PolicyFragmentDirectory { get; }

    public PolicyFragmentPolicyFile(PolicyFragmentDirectory policyFragmentDirectory)
    {
        Path = policyFragmentDirectory.Path.Append(Name);
        PolicyFragmentDirectory = policyFragmentDirectory;
    }
}

public sealed record PolicyFragmentModel
{
    public required string Name { get; init; }

    public required PolicyFragmentContractProperties Properties { get; init; }

    public sealed record PolicyFragmentContractProperties
    {
        public string? Description { get; init; }

        public JsonObject Serialize() =>
            new JsonObject()
                .AddPropertyIfNotNull("description", Description);

        public static PolicyFragmentContractProperties Deserialize(JsonObject jsonObject) =>
            new()
            {
                Description = jsonObject.TryGetStringProperty("description")
            };
    }

    public JsonObject Serialize() =>
        new JsonObject()
            .AddProperty("properties", Properties.Serialize());

    public static PolicyFragmentModel Deserialize(PolicyFragmentName name, JsonObject jsonObject) =>
        new()
        {
            Name = jsonObject.TryGetStringProperty("name") ?? name.ToString(),
            Properties = jsonObject.GetJsonObjectProperty("properties")
                                   .Map(PolicyFragmentContractProperties.Deserialize)!
        };
}