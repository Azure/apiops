using FluentAssertions;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace publisher.integration.tests;

public class PolicyFragmentPolicyFileTests
{
    private ImmutableList<FileInfo> artifacts = ImmutableList<FileInfo>.Empty;

    [OneTimeSetUp]
    public void GetPublisherArtifacts()
    {
        var publisherServiceDirectory = Fixture.PublisherServiceDirectory;
        artifacts = EnumerateArtifacts(publisherServiceDirectory).ToImmutableList();
    }

    private static IEnumerable<FileInfo> EnumerateArtifacts(ServiceDirectory serviceDirectory)
    {
        var policyFragmentsDirectoryPath = Path.Combine(serviceDirectory.Value.FullName, "policy fragments");

        return new DirectoryInfo(policyFragmentsDirectoryPath)
                .EnumerateDirectories()
                .SelectMany(policyFragmentDirectory =>
                                policyFragmentDirectory.EnumerateFiles()
                                                      .Where(file => file.Name.EndsWith("xml", StringComparison.Ordinal)));
    }

    [Test]
    public async Task ExtractorMatchesPublisher()
    {
        foreach (var artifact in artifacts)
        {
            TestContext.WriteLine($"Processing {artifact.FullName}...");

            var cancellationToken = CancellationToken.None;
            var extractorFile = GetExtractorArtifact(artifact);
            var extractorFileContents = await extractorFile.ReadAsStringWithoutWhitespace(cancellationToken);
            var publisherFileContents = await artifact.ReadAsStringWithoutWhitespace(cancellationToken);

            extractorFileContents.Should().Be(publisherFileContents);
        }
    }

    private static FileInfo GetExtractorArtifact(FileInfo publisherArtifact)
    {
        var extractorServiceDirectory = Fixture.ExtractorServiceDirectory;

        return EnumerateArtifacts(extractorServiceDirectory)
                .Where(file => file.Name == publisherArtifact.Name)
                .Where(file => file.Directory?.Name == publisherArtifact.Directory?.Name)
                .HeadOrNone()
                .IfNoneThrow($"Could not find extractor file corresponding to publisher file {publisherArtifact.FullName}.");
    }
}