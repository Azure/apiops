using CsCheck;
using LanguageExt;
using System;
using System.Collections.Frozen;
using System.Linq;

namespace common.tests;

public abstract record ApiType
{
    public sealed record GraphQl : ApiType;
    public sealed record WebSocket : ApiType;
    public sealed record Http : ApiType;
    public sealed record Soap : ApiType;

    public static Gen<ApiType> Generate() =>
        Gen.OneOfConst<ApiType>(new GraphQl(),
                                new Http());
    //new WebSocket(),
    //new Soap());
}

public record ApiRevision
{
    public required ApiRevisionNumber Number { get; init; }
    public required Option<string> Description { get; init; }
    public required Option<Uri> ServiceUri { get; init; }
    public required Option<string> Specification { get; init; }
    public required FrozenSet<ApiPolicyModel> Policies { get; init; }
    public required FrozenSet<ApiTagModel> Tags { get; init; }

    public static Gen<ApiRevision> Generate(ApiType type, ApiName name) =>
        from number in GenerateNumber()
        from serviceUri in GenerateServiceUri(type)
        from specification in GenerateSpecification(name, type, serviceUri)
        from description in GenerateDescription().OptionOf()
        from policies in ApiPolicyModel.GenerateSet()
        from tags in ApiTagModel.GenerateSet()
        select new ApiRevision
        {
            Number = number,
            ServiceUri = serviceUri,
            Specification = specification,
            Description = description,
            Policies = type is ApiType.WebSocket ? FrozenSet<ApiPolicyModel>.Empty : policies,
            Tags = tags
        };

    private static Gen<ApiRevisionNumber> GenerateNumber() =>
        from number in Gen.Int[1, 100]
        select ApiRevisionNumber.From(number);

    public static Gen<Option<Uri>> GenerateServiceUri(ApiType type) =>
        type switch
        {
            ApiType.Http => Generator.AbsoluteUri.OptionOf(),
            ApiType.GraphQl => Generator.AbsoluteUri.OptionOf(),
            ApiType.Soap => Generator.AbsoluteUri.Select(Option<Uri>.Some),
            ApiType.WebSocket => from uri in Generator.AbsoluteUri
                                 let wssUri = new UriBuilder(uri) { Scheme = Uri.UriSchemeWss }.Uri
                                 select Option<Uri>.Some(wssUri),
            _ => Gen.Const(Option<Uri>.None)
        };

    public static Gen<Option<string>> GenerateSpecification(ApiName name, ApiType type, Option<Uri> serviceUri) =>
        type switch
        {
            ApiType.Http => Gen.Const(TestApiSpecification.OpenApi).OptionOf(),
            ApiType.Soap => Gen.Const(from uri in serviceUri
                                      select TestApiSpecification.GetWsdl(name, uri)),
            ApiType.GraphQl => Gen.Const(TestApiSpecification.GraphQl).OptionOf(),
            _ => Gen.Const(Option<string>.None)
        };

    public static Gen<string> GenerateDescription() =>
        from maximumLength in Gen.Int[1, 256]
        from lorem in Generator.Lorem
        let paragraph = lorem.Paragraph()
        select paragraph[..Math.Min(maximumLength, paragraph.Length)];

    public static Gen<FrozenSet<ApiRevision>> GenerateSet(ApiType type, ApiName name) =>
        Generate(type, name).FrozenSetOf(x => x.Number.ToInt(), 1, 5);
}

public record ApiVersion
{
    public required string Version { get; init; }
    public required VersionSetName VersionSetName { get; init; }

    public static Gen<ApiVersion> Generate(VersionSetName name) =>
        from version in GenerateVersion()
        select new ApiVersion
        {
            Version = version,
            VersionSetName = name
        };

    public static Gen<string> GenerateVersion() =>
        from number in Gen.Int[0, 100]
        select $"v{number}";
}

public record ApiModel
{
    public required ApiName Name { get; init; }
    public required ApiType Type { get; init; }
    public required string Path { get; init; }
    public required FrozenSet<ApiRevision> Revisions { get; init; }
    public required FrozenSet<ApiDiagnosticModel> Diagnostics { get; init; }
    public Option<ApiVersion> Version { get; init; } = Option<ApiVersion>.None;

    public static Gen<ApiModel> Generate() =>
        from type in ApiType.Generate()
        from name in GenerateName(type)
        from path in GeneratePath()
        from revisions in ApiRevision.GenerateSet(type, name)
        from diagnostics in ApiDiagnosticModel.GenerateSet()
        select new ApiModel
        {
            Name = name,
            Type = type,
            Path = path,
            Revisions = revisions,
            Diagnostics = diagnostics
        };

    public static Gen<ApiName> GenerateName(ApiType type) =>
        from name in type switch
        {
            // WSDL API display names must start with a letter in their spec
            ApiType.Soap => Generator.AlphaNumericStringBetween(3, 10)
                                     .Where(x => char.IsAsciiLetter(x[0])),
            _ => Generator.AlphaNumericStringBetween(3, 10)
        }
        select ApiName.From(name);

    private static Gen<string> GeneratePath() =>
        Generator.AlphaNumericStringWithLength(10);

    public static Gen<FrozenSet<ApiModel>> GenerateSet() =>
        Generate().FrozenSetOf(x => x.Name, 0, 5);
}