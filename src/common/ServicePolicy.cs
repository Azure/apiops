namespace common;

public sealed record ServicePolicyResource : IPolicyResource
{
    private ServicePolicyResource() { }

    public string SingularName { get; } = "service policy";

    public string PluralName { get; } = "service policies";

    public static ServicePolicyResource Instance { get; } = new();
}