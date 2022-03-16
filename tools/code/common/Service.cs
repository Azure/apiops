using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace common;

public sealed record ServiceName : NonEmptyString
{
    private ServiceName(string value) : base(value)
    {
    }

    public static ServiceName From(string value) => new(value);

    public static ServiceName From(ServiceInformationFile file)
    {
        var jsonObject = file.ReadAsJsonObject();
        var service = Service.FromJsonObject(jsonObject);

        return new ServiceName(service.Name);
    }
}

public sealed record ServiceUri : UriRecord
{
    private ServiceUri(Uri value) : base(value)
    {
    }

    public static ServiceUri From(Uri serviceProviderUri, ServiceName serviceName) => new(serviceProviderUri.AppendPath(serviceName));
}

public sealed record ServiceDirectory : DirectoryRecord
{
    private ServiceDirectory(RecordPath path) : base(path)
    {
    }

    public static ServiceDirectory From(string path) => new(RecordPath.From(path));
}

public sealed record ServiceInformationFile : FileRecord
{
    private static readonly string name = "serviceInformation.json";

    public ServiceDirectory ServiceDirectory { get; }

    private ServiceInformationFile(ServiceDirectory serviceDirectory) : base(serviceDirectory.Path.Append(name))
    {
        ServiceDirectory = serviceDirectory;
    }

    public static ServiceInformationFile From(ServiceDirectory serviceDirectory) => new(serviceDirectory);

    public static ServiceInformationFile? TryFrom(ServiceDirectory serviceDirectory, FileInfo? file) =>
        name.Equals(file?.Name) && serviceDirectory.PathEquals(file?.Directory)
        ? new(serviceDirectory)
        : null;
}

public sealed record Service(string Name, string Location, Service.ApiManagementServiceSkuProperties Sku, Service.ApiManagementServiceProperties Properties)
{
    public Dictionary<string, string>? Tags { get; set; }
    public string[]? Zones { get; set; }
    public ApiManagementServiceIdentity? Identity { get; set; }

    public JsonObject ToJsonObject() =>
        new JsonObject().AddProperty("name", Name)
                        .AddProperty("location", Location)
                        .AddProperty("sku", Sku.ToJsonObject())
                        .AddProperty("properties", Properties.ToJsonObject())
                        .AddPropertyIfNotNull("tags", Tags?.Aggregate(new JsonObject(), (jsonObject, kvp) => jsonObject.AddProperty(kvp.Key, kvp.Value)))
                        .AddPropertyIfNotNull("zones", Zones?.ToJsonArray(zone => JsonValue.Create(zone)))
                        .AddPropertyIfNotNull("identity", Identity?.ToJsonObject());

    public static Service FromJsonObject(JsonObject jsonObject) =>
        new(Name: jsonObject.GetStringProperty("name"),
            Location: jsonObject.GetStringProperty("location"),
            Sku: jsonObject.GetAndMapJsonObjectProperty("sku", ApiManagementServiceSkuProperties.FromJsonObject),
            Properties: jsonObject.GetAndMapJsonObjectProperty("properties", ApiManagementServiceProperties.FromJsonObject))
        {
            Tags = jsonObject.TryGetAndMapNullableJsonObjectProperty("tags",
                                                         propertyJsonObject => propertyJsonObject.Where(kvp => kvp.Value is not null)
                                                                                                 .ToDictionary(kvp => kvp.Key, kvp => kvp.Value!.GetValue<string>())),
            Zones = jsonObject.TryGetAndMapNullableJsonArrayProperty("zones", node => node.GetValue<string>()),
            Identity = jsonObject.TryGetAndMapNullableJsonObjectProperty("identity", ApiManagementServiceIdentity.FromJsonObject)
        };

    public sealed record ApiManagementServiceIdentity(string Type)
    {
        public string[]? UserAssignedIdentities { get; init; }

        public JsonObject ToJsonObject() =>
            new JsonObject().AddProperty("type", Type)
                            .AddPropertyIfNotNull("userAssignedIdentities", UserAssignedIdentities?.Aggregate(new JsonObject(), (jsonObject, identity) => jsonObject.AddProperty(identity, new JsonObject())));

        public static ApiManagementServiceIdentity FromJsonObject(JsonObject jsonObject) =>
            new(Type: jsonObject.GetStringProperty("type"))
            {
                UserAssignedIdentities = jsonObject.TryGetAndMapNullableJsonObjectProperty("userAssignedIdentities",
                                                                                           jsonObject => jsonObject.Select(kvp => kvp.Key).ToArray())
            };
    }

    public sealed record ApiManagementServiceProperties(string PublisherEmail, string PublisherName)
    {
        public AdditionalLocation[]? AdditionalLocations { get; init; }
        public ApiVersionConstraint? ApiVersionConstraint { get; init; }
        public CertificateConfiguration[]? Certificates { get; init; }
        public Dictionary<string, string>? CustomProperties { get; init; } = new();
        public bool? DisableGateway { get; init; }
        public bool? EnableClientCertificate { get; init; }
        public HostnameConfiguration[]? HostnameConfigurations { get; init; }
        public string? NotificationSenderEmail { get; init; }
        public RemotePrivateEndpointConnectionWrapper[]? PrivateEndpointConnections { get; init; }
        public string? PublicIpAddressId { get; init; }
        public string? PublicNetworkAccess { get; init; }
        public bool? Restore { get; init; }
        public VirtualNetworkConfiguration? VirtualNetworkConfiguration { get; init; }
        public string? VirtualNetworkType { get; init; }

        public JsonObject ToJsonObject() =>
            new JsonObject().AddProperty("publisherEmail", PublisherEmail)
                            .AddProperty("publisherName", PublisherName)
                            .AddPropertyIfNotNull("additionalLocations", AdditionalLocations?.ToJsonArray(location => location.ToJsonObject()))
                            .AddPropertyIfNotNull("apiVersionConstraint", ApiVersionConstraint?.ToJsonObject())
                            .AddPropertyIfNotNull("certificates", Certificates?.ToJsonArray(certificate => certificate.ToJsonObject()))
                            .AddPropertyIfNotNull("customProperties", CustomProperties?.Aggregate(new JsonObject(), (jsonObject, kvp) => jsonObject.AddProperty(kvp.Key, kvp.Value)))
                            .AddPropertyIfNotNull("disableGateway", DisableGateway)
                            .AddPropertyIfNotNull("enableClientCertificate", EnableClientCertificate)
                            .AddPropertyIfNotNull("hostnameConfigurations",
                                                  // API doesn't allow host name configurations ending with "azure-api.net"
                                                  HostnameConfigurations?.All(configuration => configuration.HostName.EndsWith("azure-api.net")) ?? false
                                                  ? null
                                                  : HostnameConfigurations?.Where(configuration => configuration.HostName.EndsWith("azure-api.net") is false)
                                                                          ?.ToJsonArray(configuration => configuration.ToJsonObject()))
                            .AddPropertyIfNotNull("notificationSenderEmail", NotificationSenderEmail)
                            .AddPropertyIfNotNull("privateEndpointConnections", PrivateEndpointConnections?.ToJsonArray(connection => connection.ToJsonObject()))
                            .AddPropertyIfNotNull("publicIpAddressId", PublicIpAddressId)
                            .AddPropertyIfNotNull("publicNetworkAccess", PublicNetworkAccess)
                            .AddPropertyIfNotNull("restore", Restore)
                            .AddPropertyIfNotNull("virtualNetworkConfiguration", VirtualNetworkConfiguration?.ToJsonObject())
                            .AddPropertyIfNotNull("virtualNetworkType", VirtualNetworkType);

        public static ApiManagementServiceProperties FromJsonObject(JsonObject jsonObject) =>
            new(PublisherEmail: jsonObject.GetStringProperty("publisherEmail"), PublisherName: jsonObject.GetStringProperty("publisherName"))
            {
                AdditionalLocations = jsonObject.TryGetAndMapNullableJsonArrayProperty("additionalLocations", node => AdditionalLocation.FromJsonObject(node.AsObject())),
                ApiVersionConstraint = jsonObject.TryGetAndMapNullableJsonObjectProperty("apiVersionConstraint", ApiVersionConstraint.FromJsonObject),
                Certificates = jsonObject.TryGetAndMapNullableJsonArrayProperty("certificates", node => CertificateConfiguration.FromJsonObject(node.AsObject())),
                CustomProperties = jsonObject.TryGetAndMapNullableJsonObjectProperty("customProperties",
                                                                         propertyJsonObject => propertyJsonObject.Where(kvp => kvp.Value is not null)
                                                                                                                 .ToDictionary(kvp => kvp.Key, kvp => kvp.Value!.GetValue<string>())),
                DisableGateway = jsonObject.TryGetNullableBoolProperty("disableGateway"),
                EnableClientCertificate = jsonObject.TryGetNullableBoolProperty("enableClientCertificate"),
                HostnameConfigurations = jsonObject.TryGetAndMapNullableJsonArrayProperty("hostnameConfigurations", node => HostnameConfiguration.FromJsonObject(node.AsObject())),
                NotificationSenderEmail = jsonObject.TryGetNullableStringProperty("notificationSenderEmail"),
                PrivateEndpointConnections = jsonObject.TryGetAndMapNullableJsonArrayProperty("privateEndpointConnections", node => RemotePrivateEndpointConnectionWrapper.FromJsonObject(node.AsObject())),
                PublicIpAddressId = jsonObject.TryGetNullableStringProperty("publicIpAddressId"),
                PublicNetworkAccess = jsonObject.TryGetNullableStringProperty("publicNetworkAccess"),
                Restore = jsonObject.TryGetNullableBoolProperty("restore"),
                VirtualNetworkConfiguration = jsonObject.TryGetAndMapNullableJsonObjectProperty("virtualNetworkConfiguration", VirtualNetworkConfiguration.FromJsonObject),
                VirtualNetworkType = jsonObject.TryGetNullableStringProperty("virtualNetworkType")
            };
    }

    public sealed record AdditionalLocation(string Location, ApiManagementServiceSkuProperties Sku)
    {
        public bool? DisableGateway { get; init; }
        public string? PublicIpAddressId { get; init; }
        public VirtualNetworkConfiguration? VirtualNetworkConfiguration { get; init; }
        public string[]? Zones { get; init; }

        public JsonObject ToJsonObject() =>
            new JsonObject().AddProperty("location", Location)
                            .AddProperty("sku", Sku.ToJsonObject())
                            .AddPropertyIfNotNull("disableGateway", DisableGateway)
                            .AddPropertyIfNotNull("publicIpAddressId", PublicIpAddressId)
                            .AddPropertyIfNotNull("virtualNetworkConfiguration", VirtualNetworkConfiguration?.ToJsonObject())
                            .AddPropertyIfNotNull("zones", Zones?.ToJsonArray(zone => JsonValue.Create(zone)));

        public static AdditionalLocation FromJsonObject(JsonObject jsonObject) =>
            new(Location: jsonObject.GetStringProperty("location"), Sku: ApiManagementServiceSkuProperties.FromJsonObject(jsonObject.GetJsonObjectProperty("sku")))
            {
                DisableGateway = jsonObject.TryGetNullableBoolProperty("disableGateway"),
                PublicIpAddressId = jsonObject.TryGetNullableStringProperty("publicIpAddressId"),
                VirtualNetworkConfiguration = jsonObject.TryGetAndMapNullableJsonObjectProperty("virtualNetworkConfiguration", VirtualNetworkConfiguration.FromJsonObject),
                Zones = jsonObject.TryGetAndMapNullableJsonArrayProperty("zones", node => node.GetValue<string>())
            };
    }

    public sealed record ApiManagementServiceSkuProperties(int Capacity)
    {
        public string? Name { get; init; }

        public JsonObject ToJsonObject() =>
            new JsonObject().AddProperty("capacity", Capacity)
                            .AddPropertyIfNotNull("name", Name);

        public static ApiManagementServiceSkuProperties FromJsonObject(JsonObject jsonObject) =>
            new(jsonObject.GetIntProperty("capacity"))
            {
                Name = jsonObject.TryGetNullableStringProperty("name")
            };
    }

    public sealed record VirtualNetworkConfiguration
    {
        public string? SubnetResourceId { get; init; }

        public JsonObject ToJsonObject() => new JsonObject().AddPropertyIfNotNull("subnetResourceId", SubnetResourceId);

        public static VirtualNetworkConfiguration FromJsonObject(JsonObject jsonObject) =>
            new()
            {
                SubnetResourceId = jsonObject.TryGetNullableStringProperty("subnetResourceId")
            };
    }

    public sealed record ApiVersionConstraint
    {
        public string? MinApiVersion { get; init; }

        public JsonObject ToJsonObject() => new JsonObject().AddPropertyIfNotNull("minApiVersion", MinApiVersion);

        public static ApiVersionConstraint FromJsonObject(JsonObject jsonObject) =>
            new()
            {
                MinApiVersion = jsonObject.TryGetNullableStringProperty("minApiVersion")
            };
    }

    public sealed record CertificateConfiguration()
    {
        public CertificateInformation? Certificate { get; init; }
        public string? CertificatePassword { get; init; }
        public string? EncodedCertificate { get; init; }
        public string? StoreName { get; init; }

        public JsonObject ToJsonObject() =>
            new JsonObject().AddPropertyIfNotNull("certificate", Certificate?.ToJsonObject())
                            .AddPropertyIfNotNull("certificatePassword", CertificatePassword)
                            .AddPropertyIfNotNull("encodedCertificate", EncodedCertificate)
                            .AddPropertyIfNotNull("storeName", StoreName);

        public static CertificateConfiguration FromJsonObject(JsonObject jsonObject) =>
            new()
            {
                Certificate = jsonObject.TryGetAndMapNullableJsonObjectProperty("certificate", CertificateInformation.FromJsonObject),
                CertificatePassword = jsonObject.TryGetNullableStringProperty("certificatePassword"),
                EncodedCertificate = jsonObject.TryGetNullableStringProperty("encodedCertificate"),
                StoreName = jsonObject.TryGetNullableStringProperty("storeName")
            };
    }

    public sealed record HostnameConfiguration(string HostName)
    {
        public CertificateInformation? Certificate { get; init; }
        public string? CertificatePassword { get; init; }
        public string? CertificateSource { get; init; }
        public string? CertificateStatus { get; init; }
        public bool? DefaultSslBinding { get; init; }
        public string? EncodedCertificate { get; init; }
        public string? IdentityClientId { get; init; }
        public string? KeyVaultId { get; init; }
        public bool? NegotiateClientCertificate { get; init; }
        public string? Type { get; init; }

        public JsonObject ToJsonObject() =>
            new JsonObject().AddPropertyIfNotNull("certificate", Certificate?.ToJsonObject())
                            .AddPropertyIfNotNull("certificatePassword", CertificatePassword)
                            .AddPropertyIfNotNull("certificateSource", CertificateSource)
                            .AddPropertyIfNotNull("certificateStatus", CertificateStatus)
                            .AddPropertyIfNotNull("defaultSslBinding", DefaultSslBinding)
                            .AddPropertyIfNotNull("encodedCertificate", EncodedCertificate)
                            .AddProperty("hostName", HostName)
                            .AddPropertyIfNotNull("identityClientId", IdentityClientId)
                            .AddPropertyIfNotNull("keyVaultId", KeyVaultId)
                            .AddPropertyIfNotNull("negotiateClientCertificate", NegotiateClientCertificate)
                            .AddPropertyIfNotNull("type", Type);

        public static HostnameConfiguration FromJsonObject(JsonObject jsonObject) =>
            new(HostName: jsonObject.GetStringProperty("hostName"))
            {
                Certificate = jsonObject.TryGetAndMapNullableJsonObjectProperty("certificate", CertificateInformation.FromJsonObject)
                                        ,
                CertificatePassword = jsonObject.TryGetNullableStringProperty("certificatePassword"),
                CertificateSource = jsonObject.TryGetNullableStringProperty("certificateSource"),
                CertificateStatus = jsonObject.TryGetNullableStringProperty("certificateStatus"),
                DefaultSslBinding = jsonObject.TryGetNullableBoolProperty("defaultSslBinding"),
                EncodedCertificate = jsonObject.TryGetNullableStringProperty("encodedCertificate"),
                IdentityClientId = jsonObject.TryGetNullableStringProperty("identityClientId"),
                KeyVaultId = jsonObject.TryGetNullableStringProperty("keyVaultId"),
                NegotiateClientCertificate = jsonObject.TryGetNullableBoolProperty("negotiateClientCertificate"),
                Type = jsonObject.TryGetNullableStringProperty("type")
            };
    }

    public sealed record CertificateInformation(string Expiry, string Subject, string Thumbprint)
    {
        public JsonObject ToJsonObject() =>
            new JsonObject().AddProperty("expiry", Expiry)
                            .AddProperty("subject", Subject)
                            .AddProperty("thumbprint", Thumbprint);

        public static CertificateInformation FromJsonObject(JsonObject jsonObject) =>
            new(Expiry: jsonObject.GetStringProperty("expiry"),
                Subject: jsonObject.GetStringProperty("subject"),
                Thumbprint: jsonObject.GetStringProperty("thumbprint"));
    }

    public sealed record RemotePrivateEndpointConnectionWrapper
    {
        public string? Id { get; init; }
        public string? Name { get; init; }
        public PrivateEndpointConnectionWrapperProperties? Properties { get; init; }
        public string? Type { get; init; }

        public JsonObject ToJsonObject() =>
            new JsonObject().AddPropertyIfNotNull("id", Id)
                            .AddPropertyIfNotNull("name", Name)
                            .AddPropertyIfNotNull("properties", Properties?.ToJsonObject())
                            .AddPropertyIfNotNull("type", Type);

        public static RemotePrivateEndpointConnectionWrapper FromJsonObject(JsonObject jsonObject) =>
            new()
            {
                Id = jsonObject.TryGetNullableStringProperty("id"),
                Name = jsonObject.TryGetNullableStringProperty("name"),
                Properties = jsonObject.TryGetAndMapNullableJsonObjectProperty("properties", PrivateEndpointConnectionWrapperProperties.FromJsonObject),
                Type = jsonObject.TryGetNullableStringProperty("type")
            };
    }

    public sealed record PrivateEndpointConnectionWrapperProperties(PrivateLinkServiceConnectionState PrivateLinkServiceConnectionState)
    {
        public JsonObject ToJsonObject() =>
            new JsonObject().AddProperty("privateLinkServiceConnectionState", PrivateLinkServiceConnectionState.ToJsonObject());

        public static PrivateEndpointConnectionWrapperProperties FromJsonObject(JsonObject jsonObject) =>
            new(jsonObject.GetAndMapJsonObjectProperty("privateLinkServiceConnectionState", PrivateLinkServiceConnectionState.FromJsonObject));
    }

    public sealed record PrivateLinkServiceConnectionState
    {
        public string? ActionsRequired { get; init; }
        public string? Description { get; init; }
        public string? Status { get; init; }

        public JsonObject ToJsonObject() =>
            new JsonObject().AddPropertyIfNotNull("actionsRequired", ActionsRequired)
                            .AddPropertyIfNotNull("description", Description)
                            .AddPropertyIfNotNull("status", Status);

        public static PrivateLinkServiceConnectionState FromJsonObject(JsonObject jsonObject) =>
            new()
            {
                ActionsRequired = jsonObject.TryGetNullableStringProperty("actionsRequired"),
                Description = jsonObject.TryGetNullableStringProperty("description"),
                Status = jsonObject.TryGetNullableStringProperty("status")
            };
    }
}
