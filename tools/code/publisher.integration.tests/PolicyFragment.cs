﻿using FluentAssertions;
using NUnit.Framework;
using NUnit.Framework.Internal;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace publisher.integration.tests;

[TestFixture]
public class PolicyFragmentPolicyFileTests
{
    [TestCaseSource(nameof(GetTestCaseData))]
    public async Task ExtractorMatchesPublisher(PolicyFragmentPolicyFile publisherFile)
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
                .Select(file => new TestCaseData(file).SetName($"{nameof(PolicyFragmentPolicyFileTests)} | Extractor matches publisher | {file.Value.Name}"));

    }

    private static IEnumerable<PolicyFragmentPolicyFile> GetPublisherFiles()
    {
        var publisherServiceDirectory = Fixture.PublisherServiceDirectory;
        var policyFragmentsDirectory = PolicyFragmentsDirectory.From(publisherServiceDirectory);

        return PolicyFragmentDirectory.ListFrom(policyFragmentsDirectory)
                                      .Select(PolicyFragmentPolicyFile.From);
    }

    private static PolicyFragmentPolicyFile GetExtractorFile(PolicyFragmentPolicyFile publisherFile)
    {
        var extractorServiceDirectory = Fixture.ExtractorServiceDirectory;
        var extractorPolicyFragmentsDirectory = PolicyFragmentsDirectory.From(extractorServiceDirectory);
        
        var publisherPolicyFragmentDirectoryName = publisherFile.GetPolicyFragmentDirectory().Value.Name;
        var extractorPolicyFragmentDirectory = PolicyFragmentDirectory.ListFrom(extractorPolicyFragmentsDirectory)
                                                                      .First(directory => directory.Value.Name == publisherPolicyFragmentDirectoryName);

        return PolicyFragmentPolicyFile.From(extractorPolicyFragmentDirectory);
    }
}