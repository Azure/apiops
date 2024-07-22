using CsCheck;
using LanguageExt;
using System.Collections.Frozen;
using System.Linq;

namespace common.tests;

public sealed record WorkspaceModel
{
    public required WorkspaceName Name { get; init; }
    public required string DisplayName { get; init; }
    public Option<string> Description { get; init; }

    public static Gen<WorkspaceModel> Generate() =>
        from name in GenerateName()
        from displayName in GenerateDisplayName()
        from description in GenerateDescription().OptionOf()
        select new WorkspaceModel
        {
            Name = name,
            DisplayName = displayName,
            Description = description
        };

    public static Gen<WorkspaceName> GenerateName() =>
        from name in Generator.AlphaNumericStringBetween(10, 20)
        select WorkspaceName.From(name);

    public static Gen<string> GenerateDisplayName() =>
        Generator.AlphaNumericStringBetween(10, 20);

    public static Gen<string> GenerateDescription() =>
        from lorem in Generator.Lorem
        select lorem.Paragraph();

    public static Gen<FrozenSet<WorkspaceModel>> GenerateSet() =>
        Generate().FrozenSetOf(x => x.Name, 0, 10)
                  .DistinctBy(x => x.DisplayName);
}