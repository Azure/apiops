using FluentAssertions;
using NUnit.Framework;
using NUnit.Framework.Internal;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace publisher.integration.tests;

[TestFixture]
public class ServicePolicyFileTests
{
    [TestCaseSource(nameof(GetTestCaseData))]
    public async Task ExtractorMatchesPublisher(ServicePolicyFile publisherFile)
    {
        var extractorFile = GetExtractorFile(publisherFile);

        var cancellationToken = CancellationToken.None;
        var extractorFileContents = await extractorFile.Value.ReadAsStringWithoutWhitespace(cancellationToken);
        var publisherFileContents = await publisherFile.Value.ReadAsStringWithoutWhitespace(cancellationToken);

        extractorFileContents.Should().Be(publisherFileContents);
    }

    private static IEnumerable<TestCaseData> GetTestCaseData()
    {
        return GetPublisherFiles()
                .Select(file => new TestCaseData(file).SetName($"{nameof(ServicePolicyFileTests)} | Extractor matches publisher | {file.Value.Name})"));
    }

    private static IEnumerable<ServicePolicyFile> GetPublisherFiles()
    {
        var publisherServiceDirectory = Fixture.PublisherServiceDirectory;
        return ServicePolicyFile.ListFrom(publisherServiceDirectory);
    }

    private static ServicePolicyFile GetExtractorFile(ServicePolicyFile publisherFile)
    {
        var extractorServiceDirectory = Fixture.ExtractorServiceDirectory;
        return ServicePolicyFile.ListFrom(extractorServiceDirectory)
                                .First(file => file.Value.Name.Equals(publisherFile.Value.Name, StringComparison.Ordinal));
    }
}