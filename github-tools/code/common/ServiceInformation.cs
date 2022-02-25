namespace common;

public static class ServiceInformation
{
    public static JsonObject FormatResponseJson(JsonObject responseJson)
    {
        var skuFormatter = (JsonObject source) =>
            new JsonObject().CopyPropertyIfValueIsNonNullFrom("name", source)
                            .CopyPropertyIfValueIsNonNullFrom("capacity", source);

        var virtualNetworkConfigurationFormatter = (JsonObject source) =>
            new JsonObject().CopyPropertyIfValueIsNonNullFrom("subnetResourceId", source);

        var additionalLocationFormatter = (JsonObject source) =>
            new JsonObject().CopyPropertyIfValueIsNonNullFrom("disableGateway", source)
                            .CopyPropertyIfValueIsNonNullFrom("location", source)
                            .CopyPropertyIfValueIsNonNullFrom("publicIpAddressId", source)
                            .CopyObjectPropertyIfValueIsNonNullFrom("sku", source, skuFormatter)
                            .CopyObjectPropertyIfValueIsNonNullFrom("virtualNetworkConfiguration", source, virtualNetworkConfigurationFormatter)
                            .CopyPropertyIfValueIsNonNullFrom("zones", source);

        var apiVersionConstraintFormatter = (JsonObject source) => new JsonObject().CopyPropertyIfValueIsNonNullFrom("minApiVersion", source);

        var certificateInformationFormatter = (JsonObject source) =>
            new JsonObject().CopyPropertyIfValueIsNonNullFrom("expiry", source)
                            .CopyPropertyIfValueIsNonNullFrom("subject", source)
                            .CopyPropertyIfValueIsNonNullFrom("thumbprint", source);

        var certificateConfigurationFormatter = (JsonObject source) =>
            new JsonObject().CopyObjectPropertyIfValueIsNonNullFrom("certificate", source, certificateInformationFormatter)
                            .CopyPropertyIfValueIsNonNullFrom("certificatePassword", source)
                            .CopyPropertyIfValueIsNonNullFrom("encodedCertificate", source)
                            .CopyPropertyIfValueIsNonNullFrom("storeName", source);

        var hostnameConfigurationFormatter = (JsonObject source) =>
            new JsonObject().CopyObjectPropertyIfValueIsNonNullFrom("certificate", source, certificateInformationFormatter)
                            .CopyPropertyIfValueIsNonNullFrom("certificatePassword", source)
                            .CopyPropertyIfValueIsNonNullFrom("certificateSource", source)
                            .CopyPropertyIfValueIsNonNullFrom("certificateStatus", source)
                            .CopyPropertyIfValueIsNonNullFrom("defaultSslBinding", source)
                            .CopyPropertyIfValueIsNonNullFrom("encodedCertificate", source)
                            .CopyPropertyIfValueIsNonNullFrom("hostName", source)
                            .CopyPropertyIfValueIsNonNullFrom("identityClientId", source)
                            .CopyPropertyIfValueIsNonNullFrom("negotiateClientCertificate", source)
                            .CopyPropertyIfValueIsNonNullFrom("type", source);

        var propertiesFormatter = (JsonObject source) =>
            new JsonObject().CopyObjectArrayPropertyIfValueIsNonNullFrom("additionalLocations", source, additionalLocationFormatter)
                            .CopyObjectPropertyIfValueIsNonNullFrom("apiVersionConstraint", source, apiVersionConstraintFormatter)
                            .CopyObjectArrayPropertyIfValueIsNonNullFrom("certificates", source, certificateConfigurationFormatter)
                            .CopyPropertyIfValueIsNonNullFrom("customProperties", source)
                            .CopyPropertyIfValueIsNonNullFrom("disableGateway", source)
                            .CopyPropertyIfValueIsNonNullFrom("enableClientCertificate", source)
                            .CopyObjectArrayPropertyIfValueIsNonNullFrom("hostnameConfigurations", source, hostnameConfigurationFormatter)
                            .CopyPropertyIfValueIsNonNullFrom("notificationSenderEmail", source)
                            .CopyPropertyIfValueIsNonNullFrom("publicIpAddressId", source)
                            .CopyPropertyIfValueIsNonNullFrom("publisherEmail", source)
                            .CopyPropertyIfValueIsNonNullFrom("publisherName", source)
                            .CopyPropertyIfValueIsNonNullFrom("restore", source)
                            .CopyObjectPropertyIfValueIsNonNullFrom("virtualNetworkConfiguration", source, virtualNetworkConfigurationFormatter)
                            .CopyPropertyIfValueIsNonNullFrom("virtualNetworkType", source);

        var identityFormatter = (JsonObject source) =>
            new JsonObject().CopyPropertyIfValueIsNonNullFrom("type", source)
                            .CopyPropertyIfValueIsNonNullFrom("userAssignedIdentities", source);

        return new JsonObject().CopyPropertyFrom("name", responseJson)
                               .CopyPropertyFrom("location", responseJson)
                               .CopyPropertyIfValueIsNonNullFrom("tags", responseJson)
                               .CopyObjectPropertyFrom("sku", responseJson, skuFormatter)
                               .CopyObjectPropertyIfValueIsNonNullFrom("identity", responseJson, identityFormatter)
                               .CopyPropertyIfValueIsNonNullFrom("zones", responseJson)
                               .CopyObjectPropertyFrom("properties", responseJson, propertiesFormatter);
    }
}
