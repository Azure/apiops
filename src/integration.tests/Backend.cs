using common;
using CsCheck;
using System.Linq;
using System.Text.Json.Nodes;

namespace integration.tests;

internal sealed record BackendModel : IDtoTestModel<BackendModel>
{
    public required ResourceName Name { get; init; }
    public required string Url { get; init; }

    public Option<string> Description { get; init; } = Option.None;
    public Option<string> Title { get; init; } = Option.None;

    public static IResourceWithDto AssociatedResource { get; } = BackendResource.Instance;

    public static Gen<ModelNodeSet> GenerateNodes(ResourceModels baseline)
    {
        var newGenerator = from model in Generate()
                           select (model, ModelNodeSet.Empty);

        return Generator.GenerateNodes(baseline, GenerateUpdate, newGenerator);
    }

    private static Gen<BackendModel> Generate() =>
        from name in Generator.ResourceName
        from description in GenerateDescription().OptionOf()
        from title in GenerateTitle().OptionOf()
        from url in GenerateUrl()
        select new BackendModel
        {
            Name = name,
            Description = description,
            Title = title,
            Url = url
        };

    private static Gen<string> GenerateDescription() =>
        from lorem in Generator.Lorem
        select lorem.Paragraph();

    private static Gen<string> GenerateTitle() =>
        from lorem in Generator.Lorem
        select lorem.Sentence();

    private static Gen<string> GenerateUrl() =>
        from uri in Generator.Uri
        select uri.ToString();

    private static Gen<BackendModel> GenerateUpdate(BackendModel model) =>
        from description in GenerateDescription().OptionOf().OrConst(model.Description)
        from title in GenerateTitle().OptionOf().OrConst(model.Title)
        from url in GenerateUrl().OrConst(model.Url)
        select model with
        {
            Description = description,
            Title = title,
            Url = url
        };

    public JsonObject SerializeDto(ModelNodeSet predecessors) =>
        JsonObjectModule.From(new BackendDto()
        {
            Properties = new BackendDto.BackendContract
            {
                Description = Description.IfNoneNull(),
                Protocol = "http",
                Title = Title.IfNoneNull(),
                Url = Url
            }
        }, AssociatedResource.SerializerOptions).IfErrorThrow();

    public bool MatchesDto(JsonObject json, Option<JsonObject> overrideJson)
    {
        var jsonDto = JsonNodeModule.To<BackendDto>(json, AssociatedResource.SerializerOptions)
                                    .IfErrorNull();

        var overrideDto = overrideJson.Bind(json => JsonNodeModule.To<BackendDto>(json, AssociatedResource.SerializerOptions)
                                                                  .ToOption())
                                      .IfNoneNull();

        var left = new
        {
            Description = overrideDto?.Properties?.Description ?? Description,
            Title = overrideDto?.Properties?.Title ?? Title,
            Url = overrideDto?.Properties?.Url ?? Url
        };

        var right = new
        {
            Description = jsonDto?.Properties?.Description,
            Title = jsonDto?.Properties?.Title,
            Url = jsonDto?.Properties?.Url
        };

        return left.Description.FuzzyEquals(right.Description)
               && left.Title.FuzzyEquals(right.Title)
               && left.Url.FuzzyEquals(right.Url);
    }
}
