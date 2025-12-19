using AwesomeAssertions;
using AwesomeAssertions.Execution;
using AwesomeAssertions.Primitives;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace common.tests;

public sealed class OptionAssertions<T>(Option<T> subject, AssertionChain assertionChain) : ReferenceTypeAssertions<Option<T>, OptionAssertions<T>>(subject, assertionChain)
{
    protected override string Identifier { get; } = "option";

    public AndWhichConstraint<OptionAssertions<T>, T> BeSome([StringSyntax("CompositeFormat")] string because = "", params object[] becauseArgs)
    {
        CurrentAssertionChain
            .BecauseOf(because, becauseArgs)
            .ForCondition(Subject.IsSome)
            .FailWith("Expected {context:option} to be some{reason}, but it was none.");

        return new AndWhichConstraint<OptionAssertions<T>, T>(
            this,
            Subject.IfNoneThrow(() => new UnreachableException()));
    }

    public AndConstraint<OptionAssertions<T>> BeNone([StringSyntax("CompositeFormat")] string because = "", params object[] becauseArgs)
    {
        CurrentAssertionChain
            .BecauseOf(because, becauseArgs)
            .ForCondition(Subject.IsNone)
            .FailWith("Expected {context:option} to be none{reason}, but it was some with value {0}.",
                       () => Subject.Match(value => value, () => throw new UnreachableException()));

        return new AndConstraint<OptionAssertions<T>>(this);
    }

    public AndConstraint<OptionAssertions<T>> Be(Option<T> expected, [StringSyntax("CompositeFormat")] string because = "", params object[] becauseArgs)
    {
        CurrentAssertionChain
            .BecauseOf(because, becauseArgs)
            .ForCondition(Subject.Equals(expected))
            .FailWith("Expected {context:option} to be {0}{reason}, but it was {1}.", expected, Subject);

        return new AndConstraint<OptionAssertions<T>>(this);
    }
}

public static class OptionAssertionsExtensions
{
    extension<T>(Option<T> subject)
    {
        public OptionAssertions<T> Should() =>
            new(subject, AssertionChain.GetOrCreate());
    }
}