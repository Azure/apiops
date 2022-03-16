using System;
using System.IO;
using System.Text.Json.Nodes;

namespace common;

public sealed record NamedValueName : NonEmptyString
{
    private NamedValueName(string value) : base(value)
    {
    }

    public static NamedValueName From(string value) => new(value);

    public static NamedValueName From(NamedValueInformationFile file)
    {
        var jsonObject = file.ReadAsJsonObject();
        var namedValue = NamedValue.FromJsonObject(jsonObject);

        return new NamedValueName(namedValue.Name);
    }
}

public sealed record NamedValueDisplayName : NonEmptyString
{
    private NamedValueDisplayName(string value) : base(value)
    {
    }

    public static NamedValueDisplayName From(string value) => new(value);

    public static NamedValueDisplayName From(NamedValueInformationFile file)
    {
        var jsonObject = file.ReadAsJsonObject();
        var namedValue = NamedValue.FromJsonObject(jsonObject);

        return new NamedValueDisplayName(namedValue.Properties.DisplayName);
    }
}

public sealed record NamedValueUri : UriRecord
{
    public NamedValueUri(Uri value) : base(value)
    {
    }

    public static NamedValueUri From(ServiceUri serviceUri, NamedValueName namedValueName) =>
        new(UriExtensions.AppendPath(serviceUri, "namedValues").AppendPath(namedValueName));
}

public sealed record NamedValuesDirectory : DirectoryRecord
{
    private static readonly string name = "named values";

    public ServiceDirectory ServiceDirectory { get; }

    private NamedValuesDirectory(ServiceDirectory serviceDirectory) : base(serviceDirectory.Path.Append(name))
    {
        ServiceDirectory = serviceDirectory;
    }

    public static NamedValuesDirectory From(ServiceDirectory serviceDirectory) => new(serviceDirectory);

    public static NamedValuesDirectory? TryFrom(ServiceDirectory serviceDirectory, DirectoryInfo? directory) =>
        name.Equals(directory?.Name) && serviceDirectory.PathEquals(directory.Parent)
        ? new(serviceDirectory)
        : null;
}

public sealed record NamedValueDirectory : DirectoryRecord
{
    public NamedValuesDirectory NamedValuesDirectory { get; }
    public NamedValueDisplayName NamedValueDisplayName { get; }

    private NamedValueDirectory(NamedValuesDirectory namedValuesDirectory, NamedValueDisplayName namedValueDisplayName) : base(namedValuesDirectory.Path.Append(namedValueDisplayName))
    {
        NamedValuesDirectory = namedValuesDirectory;
        NamedValueDisplayName = namedValueDisplayName;
    }

    public static NamedValueDirectory From(NamedValuesDirectory namedValuesDirectory, NamedValueDisplayName namedValueDisplayName) => new(namedValuesDirectory, namedValueDisplayName);

    public static NamedValueDirectory? TryFrom(ServiceDirectory serviceDirectory, DirectoryInfo? directory)
    {
        var parentDirectory = directory?.Parent;
        if (parentDirectory is not null)
        {
            var namedValuesDirectory = NamedValuesDirectory.TryFrom(serviceDirectory, parentDirectory);

            return namedValuesDirectory is null ? null : From(namedValuesDirectory, NamedValueDisplayName.From(directory!.Name));
        }
        else
        {
            return null;
        }
    }
}

public sealed record NamedValueInformationFile : FileRecord
{
    private static readonly string name = "namedValueInformation.json";

    public NamedValueDirectory NamedValueDirectory { get; }

    private NamedValueInformationFile(NamedValueDirectory namedValueDirectory) : base(namedValueDirectory.Path.Append(name))
    {
        NamedValueDirectory = namedValueDirectory;
    }

    public static NamedValueInformationFile From(NamedValueDirectory namedValueDirectory) => new(namedValueDirectory);

    public static NamedValueInformationFile? TryFrom(ServiceDirectory serviceDirectory, FileInfo file)
    {
        if (name.Equals(file.Name))
        {
            var namedValueDirectory = NamedValueDirectory.TryFrom(serviceDirectory, file.Directory);

            return namedValueDirectory is null ? null : new(namedValueDirectory);
        }
        else
        {
            return null;
        }
    }
}

public sealed record NamedValue(string Name, NamedValue.NamedValueCreateContractProperties Properties)
{
    public JsonObject ToJsonObject() =>
        new JsonObject().AddProperty("name", Name)
                        .AddProperty("properties", Properties.ToJsonObject());

    public static NamedValue FromJsonObject(JsonObject jsonObject) =>
        new(Name: jsonObject.GetStringProperty("name"),
            Properties: jsonObject.GetAndMapJsonObjectProperty("properties", NamedValueCreateContractProperties.FromJsonObject));

    public sealed record NamedValueCreateContractProperties(string DisplayName)
    {
        public KeyVaultContractCreateProperties? KeyVault { get; init; }
        public bool? Secret { get; init; }
        public string[]? Tags { get; init; }
        public string? Value { get; init; }

        public JsonObject ToJsonObject() =>
            new JsonObject().AddProperty("displayName", DisplayName)
                            .AddPropertyIfNotNull("keyVault", KeyVault?.ToJsonObject())
                            .AddPropertyIfNotNull("secret", Secret)
                            .AddPropertyIfNotNull("tags", Tags?.ToJsonArray(method => JsonValue.Create(method)))
                            .AddPropertyIfNotNull("value", Value);

        public static NamedValueCreateContractProperties FromJsonObject(JsonObject jsonObject) =>
            new(DisplayName: jsonObject.GetStringProperty("displayName"))
            {
                KeyVault = jsonObject.TryGetAndMapNullableJsonObjectProperty("keyVault", KeyVaultContractCreateProperties.FromJsonObject),
                Secret = jsonObject.TryGetNullableBoolProperty("secret"),
                Tags = jsonObject.TryGetAndMapNullableJsonArrayProperty("tags", node => node.GetValue<string>()),
                Value = jsonObject.TryGetNullableStringProperty("value")
            };

    }

    public sealed record KeyVaultContractCreateProperties
    {
        public string? IdentityClientId { get; init; }
        public string? SecretIdentifier { get; init; }

        public JsonObject ToJsonObject() =>
            new JsonObject().AddPropertyIfNotNull("identityClientId", IdentityClientId)
                            .AddPropertyIfNotNull("secretIdentifier", SecretIdentifier);

        public static KeyVaultContractCreateProperties FromJsonObject(JsonObject jsonObject) =>
            new()
            {
                IdentityClientId = jsonObject.TryGetNullableStringProperty("identityClientId"),
                SecretIdentifier = jsonObject.TryGetNullableStringProperty("secretIdentifier")
            };
    }

    public static Uri GetListByServiceUri(ServiceUri serviceUri) => UriExtensions.AppendPath(serviceUri, "namedValues");
}