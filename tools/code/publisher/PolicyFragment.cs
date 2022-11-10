using common;
using Microsoft.Extensions.Logging;
using MoreLinq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace publisher;

internal static class PolicyFragment
{
    public static async ValueTask ProcessDeletedArtifacts(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory, ServiceUri serviceUri, PutRestResource putRestResource, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        var configurationPolicyFragments = GetConfigurationPolicyFragments(configurationJson);

        await GetPolicyFragmentFiles(files, serviceDirectory)
                .LeftJoin(configurationPolicyFragments,
                          firstKeySelector: policyFragment => policyFragment.Name,
                          secondKeySelector: configurationArtifact => configurationArtifact.Name,
                          firstSelector: policyFragment => (policyFragment.Name, policyFragment.InformationFile, policyFragment.PolicyFile, ConfigurationJson: (JsonObject?)null),
                          bothSelector: (file, configurationArtifact) => (file.Name, file.InformationFile, file.PolicyFile, ConfigurationJson: configurationArtifact.Json))
                .ForEachParallel(async artifact => await ProcessDeletedPolicyFragment(artifact.Name, artifact.InformationFile, artifact.PolicyFile, artifact.ConfigurationJson, serviceUri, putRestResource, deleteRestResource, logger, cancellationToken),
                                 cancellationToken);
    }

    private static IEnumerable<(PolicyFragmentName Name, JsonObject Json)> GetConfigurationPolicyFragments(JsonObject configurationJson)
    {
        return configurationJson.TryGetJsonArrayProperty("policyFragments")
                                .IfNullEmpty()
                                .Choose(node => node as JsonObject)
                                .Choose(jsonObject =>
                                {
                                    var name = jsonObject.TryGetStringProperty("name");
                                    return name is null
                                            ? null as (PolicyFragmentName, JsonObject)?
                                            : (new PolicyFragmentName(name), jsonObject);
                                });
    }

    private static IEnumerable<(PolicyFragmentName Name, PolicyFragmentInformationFile? InformationFile, PolicyFragmentPolicyFile? PolicyFile)> GetPolicyFragmentFiles(IReadOnlyCollection<FileInfo> files, ServiceDirectory serviceDirectory)
    {
        var informationFiles = files.Choose(file => TryGetInformationFile(file, serviceDirectory))
                                    .Select(file => (Name: GetPolicyFragmentName(file), File: file));

        var policyFiles = files.Choose(file => TryGetPolicyFile(file, serviceDirectory))
                               .Select(file => (Name: GetPolicyFragmentName(file), File: file));

        return informationFiles.FullJoin(policyFiles,
                                         firstKeySelector: informationFile => informationFile.Name,
                                         secondKeySelector: policyFile => policyFile.Name,
                                         firstSelector: informationFile => (informationFile.Name, (PolicyFragmentInformationFile?)informationFile.File, (PolicyFragmentPolicyFile?)null),
                                         secondSelector: policyFile => (policyFile.Name, null, policyFile.File),
                                         bothSelector: (informationFile, policyFile) => (informationFile.Name, informationFile.File, policyFile.File));
    }

    private static PolicyFragmentInformationFile? TryGetInformationFile(FileInfo? file, ServiceDirectory serviceDirectory)
    {
        if (file is null || file.Name.Equals(PolicyFragmentInformationFile.Name) is false)
        {
            return null;
        }

        var policyFragmentDirectory = TryGetPolicyFragmentDirectory(file.Directory, serviceDirectory);

        return policyFragmentDirectory is null
                ? null
                : new PolicyFragmentInformationFile(policyFragmentDirectory);
    }

    public static PolicyFragmentDirectory? TryGetPolicyFragmentDirectory(DirectoryInfo? directory, ServiceDirectory serviceDirectory)
    {
        if (directory is null)
        {
            return null;
        }

        var policyFragmentsDirectory = TryGetPolicyFragmentsDirectory(directory.Parent, serviceDirectory);
        if (policyFragmentsDirectory is null)
        {
            return null;
        }

        var policyFragmentName = new PolicyFragmentName(directory.Name);
        return new PolicyFragmentDirectory(policyFragmentName, policyFragmentsDirectory);
    }

    private static PolicyFragmentsDirectory? TryGetPolicyFragmentsDirectory(DirectoryInfo? directory, ServiceDirectory serviceDirectory)
    {
        return directory is null
            || directory.Name.Equals(PolicyFragmentsDirectory.Name) is false
            || serviceDirectory.PathEquals(directory.Parent) is false
            ? null
            : new PolicyFragmentsDirectory(serviceDirectory);
    }

    private static PolicyFragmentPolicyFile? TryGetPolicyFile(FileInfo? file, ServiceDirectory serviceDirectory)
    {
        if (file is null || file.Name.Equals(PolicyFragmentPolicyFile.Name) is false)
        {
            return null;
        }

        var policyFragmentDirectory = TryGetPolicyFragmentDirectory(file.Directory, serviceDirectory);

        return policyFragmentDirectory is null
                ? null
                : new PolicyFragmentPolicyFile(policyFragmentDirectory);
    }

    private static PolicyFragmentName GetPolicyFragmentName(PolicyFragmentInformationFile file)
    {
        return new(file.PolicyFragmentDirectory.GetName());
    }

    private static PolicyFragmentName GetPolicyFragmentName(PolicyFragmentPolicyFile file)
    {
        return new(file.PolicyFragmentDirectory.GetName());
    }

    private static async ValueTask ProcessDeletedPolicyFragment(PolicyFragmentName policyFragmentName, PolicyFragmentInformationFile? deletedInformationFile, PolicyFragmentPolicyFile? deletedPolicyFile, JsonObject? configurationJson, ServiceUri serviceUri, PutRestResource putRestResource, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        switch (deletedInformationFile, deletedPolicyFile)
        {
            // Nothing was deleted
            case (null, null):
                return;
            // Only policy file was deleted, put policy fragment with existing information file
            case (null, not null):
                var existingInformationFile = TryGetExistingInformationFile(deletedPolicyFile.PolicyFragmentDirectory);
                if (existingInformationFile is null)
                {
                    await Delete(policyFragmentName, serviceUri, deleteRestResource, logger, cancellationToken);
                }
                else
                {
                    await PutPolicyFragment(policyFragmentName, existingInformationFile, policyFile: null, configurationJson, serviceUri, putRestResource, logger, cancellationToken);
                }

                return;
            // Only information file was deleted, put policy fragment with existing policy file
            case (not null, null):
                var existingPolicyFile = TryGetExistingPolicyFile(deletedInformationFile.PolicyFragmentDirectory);
                if (existingPolicyFile is null)
                {
                    await Delete(policyFragmentName, serviceUri, deleteRestResource, logger, cancellationToken);
                }
                else
                {
                    await PutPolicyFragment(policyFragmentName, informationFile: null, existingPolicyFile, configurationJson, serviceUri, putRestResource, logger, cancellationToken);
                }

                return;
            // Both information and policy file were deleted, delete policy fragment.
            case (not null, not null):
                await Delete(policyFragmentName, serviceUri, deleteRestResource, logger, cancellationToken);
                return;
        }
    }

    private static PolicyFragmentInformationFile? TryGetExistingInformationFile(PolicyFragmentDirectory policyFragmentDirectory)
    {
        var file = new PolicyFragmentInformationFile(policyFragmentDirectory);
        return file.Exists() ? file : null;
    }

    private static async ValueTask Delete(PolicyFragmentName policyFragmentName, ServiceUri serviceUri, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        var uri = GetPolicyFragmentUri(policyFragmentName, serviceUri);

        logger.LogInformation("Deleting policyFragment {policyFragmentName}...", policyFragmentName);
        await deleteRestResource(uri.Uri, cancellationToken);
    }

    public static PolicyFragmentUri GetPolicyFragmentUri(PolicyFragmentName policyFragmentName, ServiceUri serviceUri)
    {
        var policyFragmentsUri = new PolicyFragmentsUri(serviceUri);
        return new PolicyFragmentUri(policyFragmentName, policyFragmentsUri);
    }

    private static async ValueTask PutPolicyFragment(PolicyFragmentName policyFragmentName, PolicyFragmentInformationFile? informationFile, PolicyFragmentPolicyFile? policyFile, JsonObject? configurationJson, ServiceUri serviceUri, PutRestResource putRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        if (informationFile is null && policyFile is null && configurationJson is null)
        {
            return;
        }

        logger.LogInformation("Putting policyFragment {policyFragmentName}...", policyFragmentName);

        var uri = GetPolicyFragmentUri(policyFragmentName, serviceUri);
        var json = await GetPolicyFragmentJson(policyFragmentName, informationFile, policyFile, configurationJson, cancellationToken);
        await putRestResource(uri.Uri, json, cancellationToken);
    }

    private static async ValueTask<JsonObject> GetPolicyFragmentJson(PolicyFragmentName policyFragmentName, PolicyFragmentInformationFile? informationFile, PolicyFragmentPolicyFile? policyFile, JsonObject? configurationJson, CancellationToken cancellationToken)
    {
        var policyFragmentJson = new JsonObject();

        if (informationFile is not null)
        {
            var fileJson = informationFile.ReadAsJsonObject();
            policyFragmentJson = policyFragmentJson.Merge(fileJson);
        }

        if (policyFile is not null)
        {
            var policyJson = new JsonObject
            {
                ["properties"] = new JsonObject
                {
                    ["format"] = "xml",
                    ["value"] = await policyFile.ReadAsString(cancellationToken)
                }
            };
            policyFragmentJson = policyFragmentJson.Merge(policyJson);
        }

        if (configurationJson is not null)
        {
            policyFragmentJson = policyFragmentJson.Merge(configurationJson);
        }

        if (policyFragmentJson.Any() is false)
        {
            throw new InvalidOperationException($"Policy fragment {policyFragmentName} has an empty JSON object.");
        }

        return policyFragmentJson;
    }

    private static PolicyFragmentPolicyFile? TryGetExistingPolicyFile(PolicyFragmentDirectory policyFragmentDirectory)
    {
        var file = new PolicyFragmentPolicyFile(policyFragmentDirectory);
        return file.Exists() ? file : null;
    }

    public static async ValueTask ProcessArtifactsToPut(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory, ServiceUri serviceUri, PutRestResource putRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        var configurationPolicyFragments = GetConfigurationPolicyFragments(configurationJson);

        await GetPolicyFragmentFiles(files, serviceDirectory)
                .LeftJoin(configurationPolicyFragments,
                          firstKeySelector: policyFragment => policyFragment.Name,
                          secondKeySelector: configurationArtifact => configurationArtifact.Name,
                          firstSelector: policyFragment => (policyFragment.Name, policyFragment.InformationFile, policyFragment.PolicyFile, ConfigurationJson: (JsonObject?)null),
                          bothSelector: (file, configurationArtifact) => (file.Name, file.InformationFile, file.PolicyFile, ConfigurationJson: configurationArtifact.Json))
                .ForEachParallel(async artifact => await PutPolicyFragment(artifact.Name, artifact.InformationFile, artifact.PolicyFile, artifact.ConfigurationJson, serviceUri, putRestResource, logger, cancellationToken),
                                 cancellationToken);
    }
}