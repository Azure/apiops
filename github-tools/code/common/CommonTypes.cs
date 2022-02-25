namespace common;

public struct Unit : IEquatable<Unit>
{
    public override bool Equals(object? obj)
    {
        return obj is Unit;
    }

    public bool Equals(Unit other)
    {
        return true;
    }

    public override int GetHashCode()
    {
        return 0;
    }

    public static bool operator ==(Unit left, Unit right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(Unit left, Unit right)
    {
        return !(left == right);
    }

    public static Unit Default = new Unit();
}

public abstract record NonEmptyString
{
    private readonly string value;

    protected NonEmptyString(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"'{nameof(value)}' cannot be null or whitespace.", nameof(value));
        }

        this.value = value;
    }

    public sealed override string ToString() => value;

    public static implicit operator string(NonEmptyString nonEmptyString) => nonEmptyString.ToString();
}

public record FileName : NonEmptyString
{
    private FileName(string value) : base(value)
    {
    }

    public static FileName From([NotNull] string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("File name cannot be null or whitespace.", nameof(value))
            : new FileName(value);
    }
}

public record DirectoryName: NonEmptyString
{
    private DirectoryName(string value) : base(value)
    {
    }

    public static DirectoryName From([NotNull] string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Directory name cannot be null or whitespace.", nameof(value))
            : new DirectoryName(value);
    }
}

public record ServiceName: NonEmptyString
{
    private ServiceName(string value) : base(value)
    {
    }

    public static ServiceName From([NotNull] string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Service name cannot be null or whitespace.", nameof(value))
            : new ServiceName(value);
    }
}

public record ProductName: NonEmptyString
{
    private ProductName(string value) : base(value)
    {
    }

    public static ProductName From([NotNull] string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Product name cannot be null or whitespace.", nameof(value))
            : new ProductName(value);
    }
}

public record GatewayName: NonEmptyString
{
    private GatewayName(string value) : base(value)
    {
    }

    public static GatewayName From([NotNull] string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Gateway name cannot be null or whitespace.", nameof(value))
            : new GatewayName(value);
    }
}

public record AuthorizationServerName: NonEmptyString
{
    private AuthorizationServerName(string value) : base(value)
    {
    }

    public static AuthorizationServerName From([NotNull] string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Authorization server name cannot be null or whitespace.", nameof(value))
            : new AuthorizationServerName(value);
    }
}

public record DiagnosticName : NonEmptyString
{
    private DiagnosticName(string value) : base(value)
    {
    }

    public static DiagnosticName From([NotNull] string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Diagnostic name cannot be null or whitespace.", nameof(value))
            : new DiagnosticName(value);
    }
}

public record LoggerName : NonEmptyString
{
    private LoggerName(string value) : base(value)
    {
    }

    public static LoggerName From([NotNull] string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Logger name cannot be null or whitespace.", nameof(value))
            : new LoggerName(value);
    }
}

public record ApiName : NonEmptyString
{
    private ApiName(string value) : base(value)
    {
    }

    public static ApiName From([NotNull] string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Api name cannot be null or whitespace.", nameof(value))
            : new ApiName(value);
    }
}

public record OperationName : NonEmptyString
{
    private OperationName(string value) : base(value)
    {
    }

    public static OperationName From([NotNull] string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Operation name cannot be null or whitespace.", nameof(value))
            : new OperationName(value);
    }
}

public abstract record UriRecord
{
    private readonly string value;

    protected UriRecord(Uri value)
    {
        this.value = value.ToString();
    }

    public override string ToString() => value;

    public Uri ToUri() => new Uri(value);

    public static implicit operator Uri(UriRecord record) => record.ToUri();
}

public record ServiceUri : UriRecord
{
    private ServiceUri(Uri value) : base(value)
    {
    }

    public static ServiceUri From(Uri value) => new ServiceUri(value);
}

public record ProductUri : UriRecord
{
    private ProductUri(Uri value) : base(value)
    {
    }

    public static ProductUri From(Uri value) => new ProductUri(value);
}

public record GatewayUri : UriRecord
{
    private GatewayUri(Uri value) : base(value)
    {
    }

    public static GatewayUri From(Uri value) => new GatewayUri(value);
}

public record AuthorizationServerUri : UriRecord
{
    private AuthorizationServerUri(Uri value) : base(value)
    {
    }

    public static AuthorizationServerUri From(Uri value) => new AuthorizationServerUri(value);
}

public record DiagnosticUri : UriRecord
{
    private DiagnosticUri(Uri value) : base(value)
    {
    }

    public static DiagnosticUri From(Uri value) => new DiagnosticUri(value);
}

public record LoggerUri : UriRecord
{
    private LoggerUri(Uri value) : base(value)
    {
    }

    public static LoggerUri From(Uri value) => new LoggerUri(value);
}

public record ApiUri : UriRecord
{
    private ApiUri(Uri value) : base(value)
    {
    }

    public static ApiUri From(Uri value) => new ApiUri(value);
}

public record OperationUri : UriRecord
{
    private OperationUri(Uri value) : base(value)
    {
    }

    public static OperationUri From(Uri value) => new OperationUri(value);
}

public enum ApiSpecificationFormat
{
    Json,
    Yaml
}