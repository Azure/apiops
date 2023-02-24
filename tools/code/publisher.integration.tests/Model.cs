using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace publisher.integration.tests;

public record ServiceDirectory(DirectoryInfo Value);

public record ServicePolicyFile(FileInfo Value)
{
    public static IEnumerable<ServicePolicyFile> ListFrom(ServiceDirectory serviceDirectory)
    {
        return serviceDirectory.Value
                               .EnumerateFiles()
                               .Where(file => file.Name.EndsWith("xml", StringComparison.Ordinal))
                               .Select(file => new ServicePolicyFile(file));
    }
}

public record PolicyFragmentsDirectory(DirectoryInfo Value)
{
    public static PolicyFragmentsDirectory From(ServiceDirectory serviceDirectory)
    {
        var path = Path.Combine(serviceDirectory.Value.FullName, "policy fragments");
        var directory = new DirectoryInfo(path);
        return new(directory);
    }
}

public record PolicyFragmentDirectory(DirectoryInfo Value)
{
    public static IEnumerable<PolicyFragmentDirectory> ListFrom(PolicyFragmentsDirectory policyFragmentsDirectory)
    {
        return policyFragmentsDirectory.Value
                                       .EnumerateDirectories()
                                       .Select(directory => new PolicyFragmentDirectory(directory));
    }
}

public record PolicyFragmentPolicyFile(FileInfo Value)
{
    public static PolicyFragmentPolicyFile From(PolicyFragmentDirectory policyFragmentDirectory)
    {
        var fileInfo = policyFragmentDirectory.Value
                                              .EnumerateFiles()
                                              .First(file => file.Name.Equals("policy.xml", StringComparison.Ordinal));

        return new(fileInfo);
    }

    public PolicyFragmentDirectory GetPolicyFragmentDirectory()
    {
        var directory = Value.GetNonNullableDirectory();
        return new PolicyFragmentDirectory(directory);
    }
}