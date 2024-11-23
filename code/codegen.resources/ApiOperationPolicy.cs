namespace codegen.resources;

internal sealed record ApiOperationPolicy : IPolicyResource, IChildResource
{
    public static ApiOperationPolicy Instance = new();

    public IResource Parent { get; } = ApiOperation.Instance;

    public string DtoType { get; } = "ApiOperationPolicyDto";

    public string DtoCode { get; } =
"""
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public required ApiOperationPolicyContract Properties { get; init; }

    public sealed record ApiOperationPolicyContract
    {
        [JsonPropertyName("description")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Description { get; init; }

        [JsonPropertyName("format")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Format { get; init; }

        [JsonPropertyName("value")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Value { get; init; }
    }
""";

    public string PolicyFileType { get; } = "ApiOperationPolicyFile";

    public string NameType { get; } = "ApiOperationPolicyName";

    public string NameParameter { get; } = "apiOperationPolicyName";

    public string SingularDescription { get; } = "ApiOperationPolicy";

    public string PluralDescription { get; } = "ApiOperationPolicies";

    public string LoggerSingularDescription { get; } = "API operation policy";

    public string LoggerPluralDescription { get; } = "API operation policies";

    public string CollectionUriType { get; } = "ApiOperationPoliciesUri";

    public string CollectionUriPath { get; } = "policies";

    public string UriType { get; } = "ApiOperationPolicyUri";
}
