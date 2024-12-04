using LanguageExt;
using System;

namespace common;

public sealed record ApiRevisionNumber
{
    private readonly uint value;

    private ApiRevisionNumber(uint value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, nameof(value));
        this.value = value;
    }

    public int ToInt() => (int)value;

    public override string ToString() => value.ToString();

    public static Fin<ApiRevisionNumber> From(int value) =>
        value > 0
        ? new ApiRevisionNumber((uint)value)
        : Fin<ApiRevisionNumber>.Fail("The revision number must be greater than zero.");

    public static Fin<ApiRevisionNumber> From(string? value) =>
        uint.TryParse(value, out var revisionNumber) && revisionNumber > 0
        ? new ApiRevisionNumber(revisionNumber)
        : Fin<ApiRevisionNumber>.Fail("The revision number must be an integer greater than zero.");
}

public static class ApiRevisionModule
{
    private const string revisionSeparator = ";rev=";

    public static Fin<ApiRevisionNumber> GetRevisionNumber(ApiName name) =>
        GetRootNameAndRevisionNumber(name).Map(x => x.RevisionNumber);

    private static Fin<(ApiName RootName, ApiRevisionNumber RevisionNumber)> GetRootNameAndRevisionNumber(ApiName name) =>
        name.ToString().Split(revisionSeparator) switch
        {
        [var rootNameString, var revisionNumberString] => from rootName in ApiName.From(rootNameString)
                                                          from revisionNumber in ApiRevisionNumber.From(revisionNumberString)
                                                          select (rootName, revisionNumber),
            _ => Fin<(ApiName, ApiRevisionNumber)>.Fail("The revision number is missing.")
        };

    public static ApiName RemoveRevisionNumber(ApiName name) =>
        GetRootNameAndRevisionNumber(name)
            .Map(x => x.RootName)
            .IfFail(name);

    public static Fin<ApiName> AddRevisionNumber(ApiName name, ApiRevisionNumber revisionNumber) =>
        GetRootNameAndRevisionNumber(name)
            .Match(x => Fin<ApiName>.Fail($"API name {name} already contains a revision number."),
                   _ => ApiName.From($"{name}{revisionSeparator}{revisionNumber}"));

    public static bool IsRevisioned(ApiName name) =>
        GetRootNameAndRevisionNumber(name).IsSucc;

    public static bool IsNotRevisioned(ApiName name) =>
        GetRootNameAndRevisionNumber(name).IsFail;
}