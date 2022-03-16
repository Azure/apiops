using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace common;

public sealed record ApiDiagnosticName : NonEmptyString
{
    private ApiDiagnosticName(string value) : base(value)
    {
    }

    public static ApiDiagnosticName From(string value) => new(value);
}

public sealed record ApiDiagnosticsFile : FileRecord
{
    private static readonly string name = "diagnostics.json";

    public ApiDirectory ApiDirectory { get; }

    private ApiDiagnosticsFile(ApiDirectory apiDirectory) : base(apiDirectory.Path.Append(name))
    {
        ApiDirectory = apiDirectory;
    }

    public static ApiDiagnosticsFile From(ApiDirectory apiDirectory) => new(apiDirectory);

    public static ApiDiagnosticsFile? TryFrom(ServiceDirectory serviceDirectory, FileInfo file)
    {
        if (name.Equals(file.Name))
        {
            var apiDirectory = ApiDirectory.TryFrom(serviceDirectory, file.Directory);

            return apiDirectory is null ? null : new(apiDirectory);
        }
        else
        {
            return null;
        }
    }
}

public sealed record ApiDiagnosticUri : UriRecord
{
    public ApiDiagnosticUri(Uri value) : base(value)
    {
    }

    public static ApiDiagnosticUri From(ApiUri apiUri, ApiDiagnosticName apiDiagnosticName) =>
        new(UriExtensions.AppendPath(apiUri, "diagnostics").AppendPath(apiDiagnosticName));
}

public sealed record ApiDiagnostic([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("properties")] ApiDiagnostic.DiagnosticContractProperties Properties)
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

    public JsonObject ToJsonObject() =>
        JsonSerializer.SerializeToNode(this)?.AsObject() ?? throw new InvalidOperationException("Could not serialize object.");

    public static ApiDiagnostic FromJsonObject(JsonObject jsonObject) =>
        JsonSerializer.Deserialize<ApiDiagnostic>(jsonObject) ?? throw new InvalidOperationException("Could not deserialize object.");

    public static Uri GetListByApiUri(ApiUri apiUri) => UriExtensions.AppendPath(apiUri, "diagnostics");
}
