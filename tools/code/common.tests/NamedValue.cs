using CsCheck;
using LanguageExt;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Linq;

namespace common.tests;

public abstract record NamedValueType
{
    public sealed record Default : NamedValueType
    {
        public required string Value { get; init; }
    }

    public sealed record Secret : NamedValueType
    {
        public required string Value { get; init; }
    }

    public sealed record KeyVault : NamedValueType
    {
        public required string SecretIdentifier { get; init; }
        public Option<string> IdentityClientId { get; init; } = Option<string>.None;
    }

    public static Gen<NamedValueType.Default> GenerateDefault() =>
        from value in Generator.AlphaNumericStringBetween(1, 100)
        select new NamedValueType.Default { Value = value };

    public static Gen<NamedValueType.Secret> GenerateSecret() =>
        from value in Generator.AlphaNumericStringBetween(1, 100)
        select new NamedValueType.Secret { Value = value };

    public static Gen<NamedValueType.KeyVault> GenerateKeyVault() =>
        from secretIdentifier in Generator.AlphaNumericStringBetween(1, 100)
        from identityClientId in Generator.AlphaNumericStringBetween(1, 100).OptionOf()
        select new NamedValueType.KeyVault { SecretIdentifier = secretIdentifier, IdentityClientId = identityClientId };

    public static Gen<NamedValueType> Generate() =>
        Gen.OneOf<NamedValueType>(GenerateDefault(), GenerateSecret());
}

public record NamedValueModel
{
    public required NamedValueType Type { get; init; }
    public required NamedValueName Name { get; init; }
    public required ImmutableArray<string> Tags { get; init; } = [];

    public static Gen<NamedValueName> GenerateName() =>
        from name in Generator.AlphaNumericStringBetween(10, 20)
        select NamedValueName.From(name);

    public static Gen<ImmutableArray<string>> GenerateTags() =>
        Generator.AlphaNumericStringBetween(10, 20)
                 .ImmutableArrayOf(0, 32);

    public static Gen<NamedValueModel> Generate() =>
        from type in NamedValueType.Generate()
        from name in GenerateName()
        from tags in GenerateTags()
        select new NamedValueModel
        {
            Type = type,
            Name = name,
            Tags = tags
        };

    public static Gen<FrozenSet<NamedValueModel>> GenerateSet() =>
        Generate().FrozenSetOf(x => x.Name, 0, 20);
}
