using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using CsCheck;
using LanguageExt;

namespace common.tests;


public abstract record NamedValueType
{
#pragma warning disable CA1716 // Identifiers should not match keywords
    public sealed record Default : NamedValueType
#pragma warning restore CA1716 // Identifiers should not match keywords
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
        from randomizer in Generator.Randomizer
        let value = randomizer.Word()
        select new NamedValueType.Default { Value = value };

    public static Gen<NamedValueType.Secret> GenerateSecret() =>
        from randomizer in Generator.Randomizer
        let value = randomizer.Word()
        select new NamedValueType.Secret { Value = value };

    public static Gen<NamedValueType.KeyVault> GenerateKeyVault() =>
        from randomizer in Generator.Randomizer
        let secretIdentifier = randomizer.AlphaNumeric(10)
        from identityClientId in Gen.Const(randomizer.AlphaNumeric(10)).OptionOf()
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
        from name in Generator.LoremWord
        select NamedValueName.From(name).ThrowIfFail();

    public static Gen<ImmutableArray<string>> GenerateTags() =>
        Generator.LoremWord
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
        Generate().FrozenSetOf(0, 10, Comparer);

    private static EqualityComparer<NamedValueModel> Comparer { get; } =
        EqualityComparer<NamedValueModel>.Create((first, second) => first?.Name == second?.Name,
                                                 model => model.Name.GetHashCode());
}
