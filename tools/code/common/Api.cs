using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace common;

public sealed record ApiName : NonEmptyString
{
    private ApiName(string value) : base(value)
    {
    }

    public static ApiName From(string value) => new(value);

    public static ApiName From(ApiInformationFile file)
    {
        var jsonObject = file.ReadAsJsonObject();
        var api = Api.FromJsonObject(jsonObject);

        return new ApiName(api.Name);
    }
}

public sealed record ApiDisplayName : NonEmptyString
{
    private ApiDisplayName(string value) : base(value)
    {
    }

    public static ApiDisplayName From(string value) => new(value);

    public static ApiDisplayName From(ApiInformationFile file)
    {
        var jsonObject = file.ReadAsJsonObject();
        var api = Api.FromJsonObject(jsonObject);

        return new ApiDisplayName(api.Properties.DisplayName);
    }
}

public sealed record ApiUri : UriRecord
{
    public ApiUri(Uri value) : base(value)
    {
    }

    public static ApiUri From(ServiceUri serviceUri, ApiName apiName) =>
        new(UriExtensions.AppendPath(serviceUri, "apis").AppendPath(apiName));
}

public sealed record ApisDirectory : DirectoryRecord
{
    private static readonly string name = "apis";

    private ApisDirectory(RecordPath path) : base(path)
    {
    }

    public static ApisDirectory From(ServiceDirectory serviceDirectory) =>
        new(serviceDirectory.Path.Append(name));

    public static ApisDirectory? TryFrom(ServiceDirectory serviceDirectory, DirectoryInfo? directory) =>
        name.Equals(directory?.Name) && serviceDirectory.Path.PathEquals(directory.Parent?.FullName)
        ? new(RecordPath.From(directory.FullName))
        : null;
}

public sealed record ApiInformationFile : FileRecord
{
    private static readonly string name = "apiInformation.json";

    public ApiInformationFile(RecordPath path) : base(path)
    {
    }

    public static ApiInformationFile From(ServiceDirectory serviceDirectory, ApiDisplayName displayName)
    {
        var apisDirectory = ApisDirectory.From(serviceDirectory);

        return From(apisDirectory, displayName);
    }

    public static ApiInformationFile From(ApisDirectory apiDirectory, ApiDisplayName displayName) =>
        new(apiDirectory.Path.Append(displayName).Append(name));

    public static ApiInformationFile? TryFrom(ServiceDirectory serviceDirectory, FileInfo file) =>
        name.Equals(file.Name) && ApisDirectory.TryFrom(serviceDirectory, file.Directory?.Parent) is not null
        ? new(RecordPath.From(file.FullName))
        : null;
}

public sealed record ApiSpecificationFile : FileRecord
{
    private readonly Format format;

    private ApiSpecificationFile(RecordPath path, Format format = Format.Yaml) : base(path)
    {
        this.format = format;
    }

    public static ApiSpecificationFile From(ServiceDirectory serviceDirectory, ApiDisplayName displayName, Format format = Format.Yaml)
    {
        var apisDirectory = ApisDirectory.From(serviceDirectory);

        return From(apisDirectory, displayName, format);
    }

    public static ApiSpecificationFile From(ApisDirectory apiDirectory, ApiDisplayName displayName, Format format = Format.Yaml) =>
        new(apiDirectory.Path.Append(displayName).Append(GetNameFromFormat(format)));

    public static ApiSpecificationFile? TryFrom(ServiceDirectory serviceDirectory, FileInfo file) =>
        Enum.TryParse<Format>(string.Concat(file.Extension.Skip(1)), ignoreCase: true, out var format)
            ? GetNameFromFormat(format).Equals(file.Name) && ApisDirectory.TryFrom(serviceDirectory, file.Directory?.Parent) is not null
                ? new(RecordPath.From(file.FullName), format)
                : null
            : null;

    public static async Task<Uri> GetDownloadUri(ApiUri apiUri, Func<Uri, Task<JsonObject>> getJsonObjectFromUri, Format format = Format.Yaml)
    {
        var exportUri =
            UriExtensions.SetQueryParameter(apiUri, "export", "true")
                         .SetQueryParameter("format",
                                            format switch
                                            {
                                                Format.Json => "openapi+json-link",
                                                Format.Yaml => "openapi-link",
                                                _ => throw new InvalidOperationException($"File format {format} is invalid. Only OpenAPI YAML & JSON are supported.")
                                            });

        var exportJson = await getJsonObjectFromUri(exportUri);
        var downloadUrl = exportJson.GetJsonObjectProperty("value")
                                    .GetStringProperty("link");

        return new Uri(downloadUrl);
    }

    private static string GetNameFromFormat(Format format) =>
        format switch
        {
            Format.Json => "specification.json",
            Format.Yaml => "specification.yaml",
            _ => throw new InvalidOperationException($"File format {format} is invalid.")
        };

    public enum Format
    {
        Json,
        Yaml
    }
}

public sealed record Api([property: JsonPropertyName("name")] string Name,
                         [property: JsonPropertyName("properties")] Api.ApiCreateOrUpdateProperties Properties)
{
    public sealed record ApiCreateOrUpdateProperties([property: JsonPropertyName("path")] string Path,
                                                     [property: JsonPropertyName("displayName")] string DisplayName)
    {
        [JsonPropertyName("apiRevision")]
        public string? ApiRevision { get; init; }

        [JsonPropertyName("apiRevisionDescription")]
        public string? ApiRevisionDescription { get; init; }

        [JsonPropertyName("apiType")]
        public string? ApiType { get; init; }

        [JsonPropertyName("apiVersion")]
        public string? ApiVersion { get; init; }

        [JsonPropertyName("apiVersionDescription")]
        public string? ApiVersionDescription { get; init; }

        [JsonPropertyName("apiVersionSet")]
        public ApiVersionSetContractDetails? ApiVersionSet { get; init; }

        [JsonPropertyName("apiVersionSetId")]
        public string? ApiVersionSetId { get; init; }

        [JsonPropertyName("authenticationSettings")]
        public AuthenticationSettingsContract? AuthenticationSettings { get; init; }

        [JsonPropertyName("contact")]
        public ApiContactInformation? Contact { get; init; }

        [JsonPropertyName("description")]
        public string? Description { get; init; }

        [JsonPropertyName("format")]
        public string? Format { get; init; }

        [JsonPropertyName("isCurrent")]
        public bool? IsCurrent { get; init; }

        [JsonPropertyName("license")]
        public ApiLicenseInformation? License { get; init; }

        [JsonPropertyName("protocols")]
        public string[]? Protocols { get; init; }

        [JsonPropertyName("serviceUrl")]
        public string? ServiceUrl { get; init; }

        [JsonPropertyName("sourceApiId")]
        public string? SourceApiId { get; init; }

        [JsonPropertyName("subscriptionKeyParameterNames")]
        public SubscriptionKeyParameterNamesContract? SubscriptionKeyParameterNames { get; init; }

        [JsonPropertyName("subscriptionRequired")]
        public bool? SubscriptionRequired { get; init; }

        [JsonPropertyName("termsOfServiceUrl")]
        public string? TermsOfServiceUrl { get; init; }

        [JsonPropertyName("type")]
        public string? Type { get; init; }

        [JsonPropertyName("value")]
        public string? Value { get; init; }

        [JsonPropertyName("wsdlSelector")]
        public ApiCreateOrUpdatePropertiesWsdlSelector? WsdlSelector { get; init; }
    }

    public sealed record ApiVersionSetContractDetails
    {
        [JsonPropertyName("description")]
        public string? Description { get; init; }

        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("versionHeaderName")]
        public string? VersionHeaderName { get; init; }

        [JsonPropertyName("versioningScheme")]
        public string? VersioningScheme { get; init; }

        [JsonPropertyName("versionQueryName")]
        public string? VersionQueryName { get; init; }
    }

    public sealed record AuthenticationSettingsContract
    {
        [JsonPropertyName("oAuth2")]
        public OAuth2AuthenticationSettingsContract? OAuth2 { get; init; }

        [JsonPropertyName("openid")]
        public OpenIdAuthenticationSettingsContract? Openid { get; init; }
    }

    public sealed record OAuth2AuthenticationSettingsContract
    {
        [JsonPropertyName("authorizationServerId")]
        public string? AuthorizationServerId { get; init; }

        [JsonPropertyName("scope")]
        public string? Scope { get; init; }
    }

    public sealed record OpenIdAuthenticationSettingsContract
    {
        [JsonPropertyName("bearerTokenSendingMethods")]
        public string[]? BearerTokenSendingMethods { get; init; }

        [JsonPropertyName("openidProviderId")]
        public string? OpenidProviderId { get; init; }
    }

    public sealed record ApiContactInformation
    {
        [JsonPropertyName("name")]
        public string? Email { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("url")]
        public string? Url { get; init; }
    }

    public sealed record ApiLicenseInformation
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("url")]
        public string? Url { get; init; }
    }

    public sealed record SubscriptionKeyParameterNamesContract
    {
        [JsonPropertyName("header")]
        public string? Header { get; init; }

        [JsonPropertyName("query")]
        public string? Query { get; init; }
    }

    public sealed record ApiCreateOrUpdatePropertiesWsdlSelector
    {
        [JsonPropertyName("wsdlEndpointName")]
        public string? WsdlEndpointName { get; init; }

        [JsonPropertyName("wsdlServiceName")]
        public string? WsdlServiceName { get; init; }
    }

    private static readonly JsonSerializerOptions serializerOptions = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public JsonObject ToJsonObject() =>
        JsonSerializer.SerializeToNode(this, serializerOptions)?.AsObject() ?? throw new InvalidOperationException("Could not serialize object.");

    public static Api FromJsonObject(JsonObject jsonObject) =>
        JsonSerializer.Deserialize<Api>(jsonObject, serializerOptions) ?? throw new InvalidOperationException("Could not deserialize object.");

    public static Uri GetListByServiceUri(ServiceUri serviceUri) => UriExtensions.AppendPath(serviceUri, "apis");
}