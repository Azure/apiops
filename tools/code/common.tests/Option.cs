using FluentAssertions;
using FluentAssertions.Execution;
using FluentAssertions.Primitives;
using LanguageExt;
using LanguageExt.UnsafeValueAccess;

namespace common.tests;

public static class OptionExtensions
{
    public static OptionAssertions<T> Should<T>(this Option<T> instance) where T : notnull =>
        new OptionAssertions<T>(instance);
}

public sealed class OptionAssertions<T>(Option<T> subject) : ReferenceTypeAssertions<Option<T>, OptionAssertions<T>>(subject) where T : notnull
{
    protected override string Identifier { get; } = "option";

    [CustomAssertion]
    public AndConstraint<OptionAssertions<T>> BeSome(string because = "", params object[] becauseArgs)
    {
        Execute.Assertion
               .BecauseOf(because, becauseArgs)
               .ForCondition(Subject.IsSome)
               .FailWith("Expected {context:option} to be Some{reason}, but it is None.");

        return new AndConstraint<OptionAssertions<T>>(this);
    }

    [CustomAssertion]
    public AndConstraint<OptionAssertions<T>> BeSome(T expected, string because = "", params object[] becauseArgs)
    {
        Execute.Assertion
               .BecauseOf(because, becauseArgs)
               .WithExpectation("Expected {context:option} to be Some {0}{reason}, ", expected)
               .ForCondition(Subject.IsSome)
               .FailWith("but it is None.")
               .Then
               .Given(() => Subject.ValueUnsafe())
               .ForCondition(actual => expected.Equals(actual))
               .FailWith("but it is {0}.", t => new[] { t });

        return new AndConstraint<OptionAssertions<T>>(this);
    }

    [CustomAssertion]
    public AndConstraint<OptionAssertions<T>> BeNone(string because = "", params object[] becauseArgs)
    {
        Execute.Assertion
               .BecauseOf(because, becauseArgs)
               .ForCondition(Subject.IsNone)
               .FailWith("Expected {context:option} to be None{reason}, but it is Some.");

        return new AndConstraint<OptionAssertions<T>>(this);
    }
}