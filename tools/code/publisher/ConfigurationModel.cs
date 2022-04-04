namespace publisher;

internal record ConfigurationModel
{
    public string? ApimServiceName { get; init; }
    public NamedValue[]? NamedValues { get; init; }
    public Gateway[]? Gateways { get; init; }
    public Product[]? Products { get; init; }
    public Logger[]? Loggers { get; init; }
    public Diagnostic[]? Diagnostics { get; init; }
    public Api[]? Apis { get; init; }

    public record NamedValue
    {
        public string? Name { get; init; }
        public string? DisplayName { get; init; }
        public string? Value { get; init; }
    }

    public record Gateway
    {
        public string? Name { get; init; }
        public string? Description { get; init; }
        public Api[]? Apis { get; init; }

        public record Api
        {
            public string? Name { get; init; }
        }
    };

    public record Product
    {
        public string? Name { get; init; }
        public string? DisplayName { get; init; }
        public string? Description { get; init; }
        public string? Terms { get; init; }
        public bool? SubscriptionRequired { get; init; }
        public bool? ApprovalRequired { get; init; }
        public int? SubscriptionsLimit { get; init; }
        public string? State { get; init; }
        public Api[]? Apis { get; init; }

        public record Api
        {
            public string? Name { get; init; }
        }
    }

    public record Logger
    {
        public string? Name { get; init; }
        public string? LoggerType { get; init; }
        public string? Description { get; init; }
        public bool? IsBuffered { get; init; }
        public LoggerCredentials? Credentials { get; init; }
        public string? ResourceId { get; init; }

        public record LoggerCredentials
        {
            public string? InstrumentationKey { get; init; }
            public string? Name { get; init; }
            public string? ConnectionString { get; init; }
        }
    };

    public record Diagnostic
    {
        public string? Name { get; init; }
        public string? Verbosity { get; init; }
    };

    public record Api
    {
        public string? Name { get; init; }
        public string? DisplayName { get; init; }
        public string? Description { get; init; }
        public Diagnostic[]? Diagnostics { get; init; }

        public record Diagnostic
        {
            public string? Name { get; init; }
            public string? LoggerId { get; init; }
            public string? Verbosity { get; init; }
        };
    };
}
