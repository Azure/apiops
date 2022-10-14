using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public sealed record PolicyFragmentName : NonEmptyString
{
    private PolicyFragmentName(string value) : base(value)
    {
    }

    public static PolicyFragmentName From(string value) => new(value);
}

public sealed record PolicyFragmentsDirectory : DirectoryRecord
{
    private static readonly string name = "policy fragments";

    public ServiceDirectory ServiceDirectory { get; }

    private PolicyFragmentsDirectory(ServiceDirectory serviceDirectory) : base(serviceDirectory.Path.Append(name))
    {
        ServiceDirectory = serviceDirectory;
    }

    public static PolicyFragmentsDirectory From(ServiceDirectory serviceDirectory) => new(serviceDirectory);

    public static PolicyFragmentsDirectory? TryFrom(ServiceDirectory serviceDirectory, DirectoryInfo? directory) =>
        name.Equals(directory?.Name) && serviceDirectory.PathEquals(directory.Parent)
        ? new(serviceDirectory)
        : null;
}

public sealed record PolicyFragmentDirectory : DirectoryRecord
{
    public PolicyFragmentsDirectory PolicyFragmentsDirectory { get; }
    public PolicyFragmentName PolicyFragmentName { get; }

    private PolicyFragmentDirectory(PolicyFragmentsDirectory policyFragmentsDirectory, PolicyFragmentName policyFragmentName) : base(policyFragmentsDirectory.Path.Append(policyFragmentName))
    {
        PolicyFragmentsDirectory = policyFragmentsDirectory;
        PolicyFragmentName = policyFragmentName;
    }

    public static PolicyFragmentDirectory From(PolicyFragmentsDirectory policyFragmentsDirectory, PolicyFragmentName policyFragmentName) => new(policyFragmentsDirectory, policyFragmentName);

    public static PolicyFragmentDirectory? TryFrom(ServiceDirectory serviceDirectory, DirectoryInfo? directory)
    {
        var parentDirectory = directory?.Parent;
        if (parentDirectory is not null)
        {
            var policyFragmentsDirectory = PolicyFragmentsDirectory.TryFrom(serviceDirectory, parentDirectory);

            return policyFragmentsDirectory is null ? null : From(policyFragmentsDirectory, PolicyFragmentName.From(directory!.Name));
        }
        else
        {
            return null;
        }
    }
}

public sealed record PolicyFragmentInformationFile : FileRecord
{
    private static readonly string name = "policyFragmentInformation.json";

    public PolicyFragmentDirectory PolicyFragmentDirectory { get; }

    private PolicyFragmentInformationFile(PolicyFragmentDirectory policyFragmentDirectory) : base(policyFragmentDirectory.Path.Append(name))
    {
        PolicyFragmentDirectory = policyFragmentDirectory;
    }

    public static PolicyFragmentInformationFile From(PolicyFragmentDirectory policyFragmentDirectory) => new(policyFragmentDirectory);

    public static PolicyFragmentInformationFile? TryFrom(ServiceDirectory serviceDirectory, FileInfo file)
    {
        if (name.Equals(file.Name))
        {
            var policyFragmentDirectory = PolicyFragmentDirectory.TryFrom(serviceDirectory, file.Directory);

            return policyFragmentDirectory is null ? null : new(policyFragmentDirectory);
        }
        else
        {
            return null;
        }
    }
}

public sealed record PolicyFragmentPolicyFile : FileRecord
{
    private static readonly string name = "policy.xml";

    public PolicyFragmentDirectory PolicyFragmentDirectory { get; }

    private PolicyFragmentPolicyFile(PolicyFragmentDirectory policyFragmentDirectory) : base(policyFragmentDirectory.Path.Append(name))
    {
        PolicyFragmentDirectory = policyFragmentDirectory;
    }

    public static PolicyFragmentPolicyFile From(PolicyFragmentDirectory policyFragmentDirectory) => new(policyFragmentDirectory);

    public static PolicyFragmentPolicyFile? TryFrom(ServiceDirectory serviceDirectory, FileInfo file)
    {
        if (name.Equals(file.Name))
        {
            var policyFragmentDirectory = PolicyFragmentDirectory.TryFrom(serviceDirectory, file.Directory);

            return policyFragmentDirectory is null ? null : new(policyFragmentDirectory);
        }
        else
        {
            return null;
        }
    }
}

public static class PolicyFragment
{
    private static readonly JsonSerializerOptions serializerOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    internal static Uri GetUri(ServiceProviderUri serviceProviderUri, ServiceName serviceName, PolicyFragmentName policyFragmentName) =>
        Service.GetUri(serviceProviderUri, serviceName)
               .AppendPath("policyFragments")
               .AppendPath(policyFragmentName)
               .SetQueryParameter("format", "rawxml");

    internal static Uri ListUri(ServiceProviderUri serviceProviderUri, ServiceName serviceName) =>
        Service.GetUri(serviceProviderUri, serviceName)
               .AppendPath("policyFragments");

    public static PolicyFragmentName GetNameFromFile(PolicyFragmentInformationFile file)
    {
        var jsonObject = file.ReadAsJsonObject();
        var policyFragment = Deserialize(jsonObject);

        return PolicyFragmentName.From(policyFragment.Name);
    }

    public static Models.PolicyFragment Deserialize(JsonObject jsonObject) =>
        JsonSerializer.Deserialize<Models.PolicyFragment>(jsonObject, serializerOptions) ?? throw new InvalidOperationException("Cannot deserialize JSON.");

    public static JsonObject Serialize(Models.PolicyFragment policyFragment) =>
        JsonSerializer.SerializeToNode(policyFragment, serializerOptions)?.AsObject() ?? throw new InvalidOperationException("Cannot serialize to JSON.");

    public static async ValueTask<Models.PolicyFragment> Get(Func<Uri, CancellationToken, ValueTask<JsonObject>> getResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, PolicyFragmentName policyFragmentName, CancellationToken cancellationToken)
    {
        var uri = GetUri(serviceProviderUri, serviceName, policyFragmentName);
        var json = await getResource(uri, cancellationToken);
        return Deserialize(json);
    }

    public static IAsyncEnumerable<Models.PolicyFragment> List(Func<Uri, CancellationToken, IAsyncEnumerable<JsonObject>> getResources, ServiceProviderUri serviceProviderUri, ServiceName serviceName, CancellationToken cancellationToken)
    {
        var uri = ListUri(serviceProviderUri, serviceName);
        return getResources(uri, cancellationToken).Select(Deserialize);
    }

    public static async ValueTask Put(Func<Uri, JsonObject, CancellationToken, ValueTask> putResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, Models.PolicyFragment policyFragment, CancellationToken cancellationToken)
    {
        var name = PolicyFragmentName.From(policyFragment.Name);
        var uri = GetUri(serviceProviderUri, serviceName, name);
        var json = Serialize(policyFragment);
        await putResource(uri, json, cancellationToken);
    }

    public static async ValueTask Delete(Func<Uri, CancellationToken, ValueTask> deleteResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, PolicyFragmentName policyFragmentName, CancellationToken cancellationToken)
    {
        var uri = GetUri(serviceProviderUri, serviceName, policyFragmentName);
        await deleteResource(uri, cancellationToken);
    }
}
