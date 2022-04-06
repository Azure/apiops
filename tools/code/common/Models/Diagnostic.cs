using System.Text.Json.Serialization;

namespace common.Models;

public sealed record Diagnostic([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("properties")] Diagnostic.DiagnosticContractProperties Properties)
{
    public record DiagnosticContractProperties([property: JsonPropertyName("loggerId")] string LoggerId)
    {
        [JsonPropertyName("alwaysLog")]
        public string? AlwaysLog { get; init; }

        [JsonPropertyName("backend")]
        public PipelineDiagnosticSettings? Backend { get; init; }

        [JsonPropertyName("frontend")]
        public PipelineDiagnosticSettings? Frontend { get; init; }

        [JsonPropertyName("httpCorrelationProtocol")]
        public string? HttpCorrelationProtocol { get; init; }

        [JsonPropertyName("logClientIp")]
        public bool? LogClientIp { get; init; }

        [JsonPropertyName("operationNameFormat")]
        public string? OperationNameFormat { get; init; }

        [JsonPropertyName("sampling")]
        public SamplingSettings? Sampling { get; init; }

        [JsonPropertyName("verbosity")]
        public string? Verbosity { get; init; }
    }

    public record PipelineDiagnosticSettings
    {
        [JsonPropertyName("request")]
        public HttpMessageDiagnostic? Request { get; init; }

        [JsonPropertyName("response")]
        public HttpMessageDiagnostic? Response { get; init; }
    }

    public record HttpMessageDiagnostic
    {
        [JsonPropertyName("body")]
        public BodyDiagnosticSettings? Body { get; init; }

        [JsonPropertyName("dataMasking")]
        public DataMasking? DataMasking { get; init; }

        [JsonPropertyName("headers")]
        public string[]? Headers { get; init; }
    }

    public record BodyDiagnosticSettings
    {
        [JsonPropertyName("bytes")]
        public int? Bytes { get; init; }
    }

    public record DataMasking
    {
        [JsonPropertyName("headers")]
        public DataMaskingEntity[]? Headers { get; init; }

        [JsonPropertyName("queryParams")]
        public DataMaskingEntity[]? QueryParams { get; init; }
    }

    public record DataMaskingEntity
    {
        [JsonPropertyName("mode")]
        public string? Mode { get; init; }

        [JsonPropertyName("value")]
        public string? Value { get; init; }
    }

    public record SamplingSettings
    {
        [JsonPropertyName("percentage")]
        public double? Percentage { get; init; }

        [JsonPropertyName("samplingType")]
        public string? SamplingType { get; init; }
    }
}
