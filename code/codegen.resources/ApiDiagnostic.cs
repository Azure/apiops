namespace codegen.resources;

internal sealed record ApiDiagnostic : IResourceWithName, IChildResource, IResourceWithInformationFile
{
    public static ApiDiagnostic Instance { get; } = new();

    public IResource Parent { get; } = Api.Instance;

    public string NameType { get; } = "ApiDiagnosticName";
    public string NameParameter { get; } = "apiDiagnosticName";
    public string SingularDescription { get; } = "ApiDiagnostic";
    public string PluralDescription { get; } = "ApiDiagnostics";
    public string LoggerSingularDescription { get; } = "API diagnostic";
    public string LoggerPluralDescription { get; } = "API diagnostics";
    public string CollectionDirectoryType { get; } = "ApiDiagnosticsDirectory";
    public string CollectionDirectoryName { get; } = "diagnostics";
    public string DirectoryType { get; } = "ApiDiagnosticDirectory";
    public string CollectionUriType { get; } = "ApiDiagnosticsUri";
    public string CollectionUriPath { get; } = "diagnostics";
    public string UriType { get; } = "ApiDiagnosticUri";
    public string InformationFileType { get; } = "ApiDiagnosticInformationFile";
    public string InformationFileName { get; } = "diagnosticInformation.json";
    public string DtoType { get; } = "ApiDiagnosticDto";
    public string DtoCode { get; } =
"""
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public required DiagnosticContract Properties { get; init; }

    public sealed record DiagnosticContract
    {
        [JsonPropertyName("loggerId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? LoggerId { get; init; }

        [JsonPropertyName("alwaysLog")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? AlwaysLog { get; init; }

        [JsonPropertyName("backend")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public PipelineDiagnosticSettings? Backend { get; init; }

        [JsonPropertyName("frontend")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public PipelineDiagnosticSettings? Frontend { get; init; }

        [JsonPropertyName("httpCorrelationProtocol")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? HttpCorrelationProtocol { get; init; }

        [JsonPropertyName("logClientIp")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool? LogClientIp { get; init; }

        [JsonPropertyName("metrics")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool? Metrics { get; init; }

        [JsonPropertyName("operationNameFormat")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? OperationNameFormat { get; init; }

        [JsonPropertyName("sampling")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public SamplingSettings? Sampling { get; init; }

        [JsonPropertyName("verbosity")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Verbosity { get; init; }
    }

    public sealed record PipelineDiagnosticSettings
    {
        [JsonPropertyName("request")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public HttpMessageDiagnostic? Request { get; init; }

        [JsonPropertyName("response")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public HttpMessageDiagnostic? Response { get; init; }
    }

    public sealed record HttpMessageDiagnostic
    {
        [JsonPropertyName("body")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public BodyDiagnosticSettings? Body { get; init; }

        [JsonPropertyName("dataMasking")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public DataMasking? DataMasking { get; init; }

        [JsonPropertyName("headers")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public ImmutableArray<string>? Headers { get; init; }
    }

    public sealed record BodyDiagnosticSettings
    {
        [JsonPropertyName("bytes")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int? Bytes { get; init; }
    }

    public sealed record DataMasking
    {
        [JsonPropertyName("headers")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public ImmutableArray<DataMaskingEntity>? Headers { get; init; }

        [JsonPropertyName("queryParams")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public ImmutableArray<DataMaskingEntity>? QueryParams { get; init; }
    }

    public sealed record DataMaskingEntity
    {
        [JsonPropertyName("mode")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Mode { get; init; }

        [JsonPropertyName("value")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Value { get; init; }
    }

    public sealed record SamplingSettings
    {
        [JsonPropertyName("percentage")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public float? Percentage { get; init; }

        [JsonPropertyName("samplingType")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? SamplingType { get; init; }
    }
""";
}
