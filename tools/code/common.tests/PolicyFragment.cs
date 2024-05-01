using CsCheck;
using LanguageExt;
using System.Collections.Frozen;
using System.Linq;

namespace common.tests;

public sealed record PolicyFragmentModel
{
    public required PolicyFragmentName Name { get; init; }
    public Option<string> Description { get; init; }
    public required string Content { get; init; }

    public static Gen<PolicyFragmentModel> Generate() =>
        from name in GenerateName()
        from description in GenerateDescription().OptionOf()
        from content in GenerateContent()
        select new PolicyFragmentModel
        {
            Name = name,
            Description = description,
            Content = content
        };

    public static Gen<PolicyFragmentName> GenerateName() =>
        from name in Generator.AlphaNumericStringBetween(10, 20)
        select PolicyFragmentName.From(name);

    public static Gen<string> GenerateDescription() =>
        from lorem in Generator.Lorem
        select lorem.Paragraph();

    public static Gen<string> GenerateContent() =>
        Gen.Const("""
            <fragment>
                <mock-response status-code="200" content-type="application/json" />
            </fragment>
            """);

    public static Gen<FrozenSet<PolicyFragmentModel>> GenerateSet() =>
        Generate().FrozenSetOf(x => x.Name, 0, 10);
}