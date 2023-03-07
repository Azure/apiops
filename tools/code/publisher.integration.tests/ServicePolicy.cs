using FluentAssertions;
using LanguageExt;
using NUnit.Framework;
using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace publisher.integration.tests;

[TestFixture]
public class ServicePolicyFileTests
{
    private ImmutableList<FileInfo> artifacts = ImmutableList<FileInfo>.Empty;

    [OneTimeSetUp]
    public void GetPublisherArtifacts()
    {
        var publisherServiceDirectory = Fixture.PublisherServiceDirectory;
        artifacts = GetArtifacts(publisherServiceDirectory);
    }

    private static ImmutableList<FileInfo> GetArtifacts(ServiceDirectory serviceDirectory)
    {
        return serviceDirectory.Value
                               .EnumerateFiles()
                               .Where(file => file.Name.EndsWith("xml", StringComparison.Ordinal))
                               .ToImmutableList();
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

        return extractorServiceDirectory.Value
                                        .EnumerateFiles()
                                        .Where(file => file.Name == publisherArtifact.Name)
                                        .HeadOrNone()
                                        .IfNoneThrow($"Could not find extractor file corresponding to publisher file {publisherArtifact.FullName}.");
    }
}