using common;
using CsCheck;
using System.Linq;
using System.Text.Json.Nodes;

namespace integration.tests;

internal sealed record ApiDiagnosticModel : IResourceWithReferenceTestModel<ApiDiagnosticModel>
{
    public required ResourceName Name { get; init; }
    public required ResourceName LoggerName { get; init; }
    public required float SamplingPercentage { get; init; }

    public static IResourceWithReference AssociatedResource { get; } = ApiDiagnosticResource.Instance;

    public static Gen<ModelNodeSet> GenerateNodes(ResourceModels baseline)
    {
        var option = from predecessorsGen in Generator.GeneratePredecessors<ApiDiagnosticModel>(baseline)
                     let newGenerator = from predecessors in predecessorsGen
                                        let loggerName = predecessors.PickNameOrThrow<LoggerResource>()
                                        from model in Generate(loggerName)
                                        select (model, predecessors)
                     select Generator.GenerateNodes(baseline, GenerateUpdate, newGenerator);

        return option.IfNone(() => Gen.Const(ModelNodeSet.Empty));
    }

    private static Gen<ApiDiagnosticModel> GenerateUpdate(ApiDiagnosticModel model) =>
        from samplingPercentage in GenerateSamplingPercentage().OrConst(model.SamplingPercentage)
        select model with
        {
            SamplingPercentage = samplingPercentage
        };

    private static Gen<float> GenerateSamplingPercentage() =>
        from percentage in Gen.Int[0, 100]
        select (float)percentage;

    private static Gen<ApiDiagnosticModel> Generate(ResourceName loggerName) =>
        from samplingPercentage in GenerateSamplingPercentage()
        select new ApiDiagnosticModel
        {
            Name = loggerName,
            LoggerName = loggerName,
            SamplingPercentage = samplingPercentage
        };

    public JsonObject SerializeDto(ModelNodeSet predecessors) =>
        JsonObjectModule.From(new ApiDiagnosticDto()
        {
            Properties = new ApiDiagnosticDto.DiagnosticContract
            {
                LoggerId = predecessors.First(node => node.Model.AssociatedResource is LoggerResource
                                                      && node.Model.Name == LoggerName)
                                       .ToResourceId(),
                Sampling = new ApiDiagnosticDto.SamplingSettings
                {
                    SamplingType = "fixed",
                    Percentage = SamplingPercentage
                }
            }
        }, AssociatedResource.SerializerOptions).IfErrorThrow();

    public bool MatchesDto(JsonObject json, Option<JsonObject> overrideJson)
    {
        var jsonDto = JsonNodeModule.To<ApiDiagnosticDto>(json, AssociatedResource.SerializerOptions)
                                    .IfErrorNull();

        var overrideDto = overrideJson.Bind(json => JsonNodeModule.To<ApiDiagnosticDto>(json, AssociatedResource.SerializerOptions)
                                                                  .ToOption())
                                      .IfNoneNull();

        var left = new
        {
            LoggerName = overrideDto?.Properties?.LoggerId?.Split('/')?.LastOrDefault() ?? LoggerName.ToString(),
            SamplingPercentage = overrideDto?.Properties?.Sampling?.Percentage ?? SamplingPercentage
        };

        var right = new
        {
            LoggerName = jsonDto?.Properties?.LoggerId?.Split('/')?.LastOrDefault(),
            SamplingPercentage = jsonDto?.Properties?.Sampling?.Percentage
        };

        return left.LoggerName.FuzzyEquals(right.LoggerName)
                && left.SamplingPercentage.FuzzyEquals(right.SamplingPercentage);
    }
}
