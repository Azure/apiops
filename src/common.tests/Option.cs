using System;
using System.Threading.Tasks;
using TUnit.Assertions.Core;

namespace common.tests;

public sealed class OptionIsSomeAssertion<T>(AssertionContext<Option<T>> context) : Assertion<T>(context.Map(Map))
{
    protected override string GetExpectation() => "Option to be Some.";

    protected override async Task<AssertionResult> CheckAsync(EvaluationMetadata<T> metadata)
    {
        await ValueTask.CompletedTask;

        var exception = metadata.Exception;

        if (exception is not null)
        {
            return AssertionResult.Failed(exception.Message);
        }
        else
        {
            return AssertionResult.Passed;
        }
    }

    private static T Map(Option<T>? option) =>
        option is null
            ? throw new InvalidOperationException("Option cannot be null.")
            : option.IfNone(() => throw new InvalidOperationException("Option was None."));
}

public sealed class OptionIsNoneAssertion<T>(AssertionContext<Option<T>> context) : Assertion<Option<T>>(context)
{
    protected override async Task<AssertionResult> CheckAsync(EvaluationMetadata<Option<T>> metadata)
    {
        await ValueTask.CompletedTask;

        var value = metadata.Value;
        var exception = metadata.Exception;

        if (exception is not null)
        {
            return AssertionResult.Failed($"threw {exception.GetType().Name} with message {exception.Message}.");
        }
        else if (value is null)
        {
            return AssertionResult.Failed("value was null");
        }
        else if (value.IsNone)
        {
            return AssertionResult.Passed;
        }
        else
        {
            var t = value.Match<T?>(t => t, () => default);
            return AssertionResult.Failed($"option is Some with value {t}");
        }
    }

    protected override string GetExpectation() => "to be None";
}

public static class OptionAssertionExtensions
{
    extension<T>(IAssertionSource<Option<T>> source)
    {
        public OptionIsSomeAssertion<T> IsSome()
        {
            source.Context.ExpressionBuilder.Append($".IsSome()");
            return new(source.Context);
        }

        public OptionIsNoneAssertion<T> IsNone()
        {
            source.Context.ExpressionBuilder.Append($".IsNone()");
            return new(source.Context);
        }
    }
}