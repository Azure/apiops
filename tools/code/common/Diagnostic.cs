using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace common;

public sealed record DiagnosticsUri : IArtifactUri
{
    public Uri Uri { get; }

    public DiagnosticsUri(ServiceUri serviceUri)
    {
        Uri = serviceUri.AppendPath("diagnostics");
    }
}

public sealed record DiagnosticsDirectory : IArtifactDirectory
{
    public static string Name { get; } = "diagnostics";

    public ArtifactPath Path { get; }

    public ServiceDirectory ServiceDirectory { get; }

    public DiagnosticsDirectory(ServiceDirectory serviceDirectory)
    {
        Path = serviceDirectory.Path.Append(Name);
        ServiceDirectory = serviceDirectory;
    }
}

public sealed record DiagnosticName
{
    private readonly string value;

    public DiagnosticName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Diagnostic name cannot be null or whitespace.", nameof(value));
        }

        this.value = value;
    }

    public override string ToString() => value;
}

public sealed record DiagnosticUri : IArtifactUri
{
    public Uri Uri { get; }

    public DiagnosticUri(DiagnosticName diagnosticName, DiagnosticsUri diagnosticsUri)
    {
        Uri = diagnosticsUri.AppendPath(diagnosticName.ToString());
    }
}

public sealed record DiagnosticDirectory : IArtifactDirectory
{
    public ArtifactPath Path { get; }

    public DiagnosticsDirectory DiagnosticsDirectory { get; }

    public DiagnosticDirectory(DiagnosticName diagnosticName, DiagnosticsDirectory diagnosticsDirectory)
    {
        Path = diagnosticsDirectory.Path.Append(diagnosticName.ToString());
        DiagnosticsDirectory = diagnosticsDirectory;
    }
}

public sealed record DiagnosticInformationFile : IArtifactFile
{
    public static string Name { get; } = "diagnosticInformation.json";

    public ArtifactPath Path { get; }

    public DiagnosticDirectory DiagnosticDirectory { get; }

    public DiagnosticInformationFile(DiagnosticDirectory diagnosticDirectory)
    {
        Path = diagnosticDirectory.Path.Append(Name);
        DiagnosticDirectory = diagnosticDirectory;
    }
}

public sealed record DiagnosticModel
{
    public required string Name { get; init; }

    public required DiagnosticContractProperties Properties { get; init; }

    public sealed record DiagnosticContractProperties
    {
        public AlwaysLogOption? AlwaysLog { get; init; }
        public PipelineDiagnosticSettings? Backend { get; init; }
        public PipelineDiagnosticSettings? Frontend { get; init; }
        public HttpCorrelationProtocolOption? HttpCorrelationProtocol { get; init; }
        public bool? LogClientIp { get; init; }
        public string? LoggerId { get; init; }
        public bool? Metrics { get; init; }
        public OperationNameFormatOption? OperationNameFormat { get; init; }
        public SamplingSettings? Sampling { get; init; }
        public VerbosityOption? Verbosity { get; init; }

        public JsonObject Serialize() =>
            new JsonObject()
                .AddPropertyIfNotNull("alwaysLog", AlwaysLog?.Serialize())
                .AddPropertyIfNotNull("backend", Backend?.Serialize())
                .AddPropertyIfNotNull("frontend", Frontend?.Serialize())
                .AddPropertyIfNotNull("httpCorrelationProtocol", HttpCorrelationProtocol?.Serialize())
                .AddPropertyIfNotNull("logClientIp", LogClientIp)
                .AddPropertyIfNotNull("loggerId", LoggerId)
                .AddPropertyIfNotNull("metrics", Metrics)
                .AddPropertyIfNotNull("operationNameFormat", OperationNameFormat?.Serialize())
                .AddPropertyIfNotNull("sampling", Sampling?.Serialize())
                .AddPropertyIfNotNull("verbosity", Verbosity?.Serialize());

        public static DiagnosticContractProperties Deserialize(JsonObject jsonObject) =>
            new()
            {
                AlwaysLog = jsonObject.TryGetProperty("alwaysLog")
                                      .Map(AlwaysLogOption.Deserialize),
                Backend = jsonObject.TryGetJsonObjectProperty("backend")
                                    .Map(PipelineDiagnosticSettings.Deserialize),
                Frontend = jsonObject.TryGetJsonObjectProperty("frontend")
                                    .Map(PipelineDiagnosticSettings.Deserialize),
                HttpCorrelationProtocol = jsonObject.TryGetProperty("httpCorrelationProtocol")
                                                    .Map(HttpCorrelationProtocolOption.Deserialize),
                LogClientIp = jsonObject.TryGetBoolProperty("logClientIp"),
                LoggerId = jsonObject.TryGetStringProperty("loggerId"),
                Metrics = jsonObject.TryGetBoolProperty("metrics"),
                OperationNameFormat = jsonObject.TryGetProperty("operationNameFormat")
                                      .Map(OperationNameFormatOption.Deserialize),
                Sampling = jsonObject.TryGetJsonObjectProperty("sampling")
                                     .Map(SamplingSettings.Deserialize),
                Verbosity = jsonObject.TryGetProperty("verbosity")
                                      .Map(VerbosityOption.Deserialize)
            };

        public sealed record AlwaysLogOption
        {
            private readonly string value;

            private AlwaysLogOption(string value)
            {
                this.value = value;
            }

            public static AlwaysLogOption AllErrors => new("allErrors");

            public override string ToString() => value;

            public JsonNode Serialize() => JsonValue.Create(ToString()) ?? throw new JsonException("Value cannot be null.");

            public static AlwaysLogOption Deserialize(JsonNode node) =>
                node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var value)
                    ? value switch
                    {
                        _ when nameof(AllErrors).Equals(value, StringComparison.OrdinalIgnoreCase) => AllErrors,
                        _ => throw new JsonException($"'{value}' is not a valid {nameof(AlwaysLogOption)}.")
                    }
                        : throw new JsonException("Node must be a string JSON value.");
        }

        public sealed record PipelineDiagnosticSettings
        {
            public HttpMessageDiagnostic? Request { get; init; }
            public HttpMessageDiagnostic? Response { get; init; }

            public JsonObject Serialize() =>
                new JsonObject()
                    .AddPropertyIfNotNull("request", Request?.Serialize())
                    .AddPropertyIfNotNull("response", Response?.Serialize());

            public static PipelineDiagnosticSettings Deserialize(JsonObject jsonObject) =>
                new()
                {
                    Request = jsonObject.TryGetJsonObjectProperty("request")
                                        .Map(HttpMessageDiagnostic.Deserialize),
                    Response = jsonObject.TryGetJsonObjectProperty("response")
                                         .Map(HttpMessageDiagnostic.Deserialize),
                };

            public sealed record HttpMessageDiagnostic
            {
                public BodyDiagnosticSettings? Body { get; init; }
                public DataMaskingSettings? DataMasking { get; init; }
                public string[]? Headers { get; init; }

                public JsonObject Serialize() =>
                    new JsonObject()
                        .AddPropertyIfNotNull("body", Body?.Serialize())
                        .AddPropertyIfNotNull("dataMasking", DataMasking?.Serialize())
                        .AddPropertyIfNotNull("headers", Headers?.Select(JsonNodeExtensions.FromString)
                                                                ?.ToJsonArray());

                public static HttpMessageDiagnostic Deserialize(JsonObject jsonObject) =>
                    new()
                    {
                        Body = jsonObject.TryGetJsonObjectProperty("body")
                                         .Map(BodyDiagnosticSettings.Deserialize),
                        DataMasking = jsonObject.TryGetJsonObjectProperty("dataMasking")
                                                .Map(DataMaskingSettings.Deserialize),
                        Headers = jsonObject.TryGetJsonArrayProperty("headers")
                                           ?.Choose(node => node?.GetValue<string>())
                                           ?.ToArray()
                    };

                public sealed record BodyDiagnosticSettings
                {
                    public int? Bytes { get; init; }

                    public JsonObject Serialize() =>
                        new JsonObject()
                            .AddPropertyIfNotNull("bytes", Bytes);

                    public static BodyDiagnosticSettings Deserialize(JsonObject jsonObject) =>
                        new()
                        {
                            Bytes = jsonObject.TryGetIntProperty("bytes")
                        };
                }

                public sealed record DataMaskingSettings
                {
                    public DataMaskingEntity[]? Headers { get; init; }
                    public DataMaskingEntity[]? QueryParams { get; init; }

                    public JsonObject Serialize() =>
                        new JsonObject()
                            .AddPropertyIfNotNull("headers", Headers?.Select(x => x.Serialize())
                                                                    ?.ToJsonArray())
                            .AddPropertyIfNotNull("queryParams", Headers?.Select(x => x.Serialize())
                                                                        ?.ToJsonArray());

                    public static DataMaskingSettings Deserialize(JsonObject jsonObject) =>
                        new()
                        {
                            Headers = jsonObject.TryGetJsonArrayProperty("headers")
                                               ?.Choose(node => node?.AsObject())
                                               ?.Select(DataMaskingEntity.Deserialize)
                                               ?.ToArray(),
                            QueryParams = jsonObject.TryGetJsonArrayProperty("queryParams")
                                                   ?.Choose(node => node?.AsObject())
                                                   ?.Select(DataMaskingEntity.Deserialize)
                                                   ?.ToArray(),
                        };

                    public sealed record DataMaskingEntity
                    {
                        public DataMaskingModeOption? Mode { get; init; }
                        public string? Value { get; init; }

                        public JsonObject Serialize() =>
                            new JsonObject()
                                .AddPropertyIfNotNull("mode", Mode?.Serialize())
                                .AddPropertyIfNotNull("value", Value);

                        public static DataMaskingEntity Deserialize(JsonObject jsonObject) =>
                            new()
                            {
                                Mode = jsonObject.TryGetProperty("mode")
                                                 .Map(DataMaskingModeOption.Deserialize),
                                Value = jsonObject.TryGetStringProperty("value")
                            };

                        public sealed record DataMaskingModeOption
                        {
                            private readonly string value;

                            private DataMaskingModeOption(string value)
                            {
                                this.value = value;
                            }

                            public static DataMaskingModeOption Hide => new("Hide");
                            public static DataMaskingModeOption Mask => new("Mask");

                            public override string ToString() => value;

                            public JsonNode Serialize() => JsonValue.Create(ToString()) ?? throw new JsonException("Value cannot be null.");

                            public static DataMaskingModeOption Deserialize(JsonNode node) =>
                                node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var value)
                                    ? value switch
                                    {
                                        _ when nameof(Hide).Equals(value, StringComparison.OrdinalIgnoreCase) => Hide,
                                        _ when nameof(Mask).Equals(value, StringComparison.OrdinalIgnoreCase) => Mask,
                                        _ => throw new JsonException($"'{value}' is not a valid {nameof(DataMaskingModeOption)}.")
                                    }
                                        : throw new JsonException("Node must be a string JSON value.");
                        }
                    }
                }
            }
        }

        public sealed record HttpCorrelationProtocolOption
        {
            private readonly string value;

            private HttpCorrelationProtocolOption(string value)
            {
                this.value = value;
            }

            public static HttpCorrelationProtocolOption Legacy => new("Legacy");
            public static HttpCorrelationProtocolOption None => new("None");
            public static HttpCorrelationProtocolOption W3C => new("W3C");

            public override string ToString() => value;

            public JsonNode Serialize() => JsonValue.Create(ToString()) ?? throw new JsonException("Value cannot be null.");

            public static HttpCorrelationProtocolOption Deserialize(JsonNode node) =>
                node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var value)
                    ? value switch
                    {
                        _ when nameof(Legacy).Equals(value, StringComparison.OrdinalIgnoreCase) => Legacy,
                        _ when nameof(None).Equals(value, StringComparison.OrdinalIgnoreCase) => None,
                        _ when nameof(W3C).Equals(value, StringComparison.OrdinalIgnoreCase) => W3C,
                        _ => throw new JsonException($"'{value}' is not a valid {nameof(HttpCorrelationProtocolOption)}.")
                    }
                        : throw new JsonException("Node must be a string JSON value.");
        }

        public sealed record OperationNameFormatOption
        {
            private readonly string value;

            private OperationNameFormatOption(string value)
            {
                this.value = value;
            }

            public static OperationNameFormatOption Name => new("Name");
            public static OperationNameFormatOption Url => new("Url");

            public override string ToString() => value;

            public JsonNode Serialize() => JsonValue.Create(ToString()) ?? throw new JsonException("Value cannot be null.");

            public static OperationNameFormatOption Deserialize(JsonNode node) =>
                node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var value)
                    ? value switch
                    {
                        _ when nameof(Name).Equals(value, StringComparison.OrdinalIgnoreCase) => Name,
                        _ when nameof(Url).Equals(value, StringComparison.OrdinalIgnoreCase) => Url,
                        _ => throw new JsonException($"'{value}' is not a valid {nameof(OperationNameFormatOption)}.")
                    }
                        : throw new JsonException("Node must be a string JSON value.");
        }

        public sealed record SamplingSettings
        {
            public double? Percentage { get; init; }

            public SamplingTypeOption? SamplingType { get; init; }

            public JsonObject Serialize() =>
                new JsonObject()
                    .AddPropertyIfNotNull("percentage", Percentage)
                    .AddPropertyIfNotNull("samplingType", SamplingType?.Serialize());

            public static SamplingSettings Deserialize(JsonObject jsonObject) =>
                new()
                {
                    Percentage = jsonObject.TryGetDoubleProperty("percentage"),
                    SamplingType = jsonObject.TryGetProperty("samplingType")
                                             .Map(SamplingTypeOption.Deserialize)
                };

            public sealed record SamplingTypeOption
            {
                private readonly string value;

                private SamplingTypeOption(string value)
                {
                    this.value = value;
                }

                public static SamplingTypeOption Fixed => new("Fixed");

                public override string ToString() => value;

                public JsonNode Serialize() => JsonValue.Create(ToString()) ?? throw new JsonException("Value cannot be null.");

                public static SamplingTypeOption Deserialize(JsonNode node) =>
                    node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var value)
                        ? value switch
                        {
                            _ when nameof(Fixed).Equals(value, StringComparison.OrdinalIgnoreCase) => Fixed,
                            _ => throw new JsonException($"'{value}' is not a valid {nameof(SamplingTypeOption)}.")
                        }
                            : throw new JsonException("Node must be a string JSON value.");
            }
        }

        public sealed record VerbosityOption
        {
            private readonly string value;

            private VerbosityOption(string value)
            {
                this.value = value;
            }

            public static VerbosityOption Error => new("error");
            public static VerbosityOption Information => new("information");
            public static VerbosityOption Verbose => new("verbose");

            public override string ToString() => value;

            public JsonNode Serialize() => JsonValue.Create(ToString()) ?? throw new JsonException("Value cannot be null.");

            public static VerbosityOption Deserialize(JsonNode node) =>
                node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var value)
                    ? value switch
                    {
                        _ when nameof(Error).Equals(value, StringComparison.OrdinalIgnoreCase) => Error,
                        _ when nameof(Information).Equals(value, StringComparison.OrdinalIgnoreCase) => Information,
                        _ when nameof(Verbose).Equals(value, StringComparison.OrdinalIgnoreCase) => Verbose,
                        _ => throw new JsonException($"'{value}' is not a valid {nameof(VerbosityOption)}.")
                    }
                        : throw new JsonException("Node must be a string JSON value.");
        }
    }

    public JsonObject Serialize() =>
        new JsonObject()
            .AddProperty("properties", Properties.Serialize());

    public static DiagnosticModel Deserialize(DiagnosticName name, JsonObject jsonObject) =>
        new()
        {
            Name = jsonObject.TryGetStringProperty("name") ?? name.ToString(),
            Properties = jsonObject.GetJsonObjectProperty("properties")
                                   .Map(DiagnosticContractProperties.Deserialize)!
        };
}