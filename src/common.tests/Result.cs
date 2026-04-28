using System;
using System.Threading.Tasks;
using TUnit.Assertions.Core;

namespace common.tests;

public sealed class ResultIsSuccessAssertion<T>(AssertionContext<Result<T>> context) : Assertion<T>(context.Map(Map))
{
    protected override string GetExpectation() => "result to be successful";

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

    private static T Map(Result<T>? result) =>
        result is null
            ? throw new InvalidOperationException("Result cannot be null.")
            : result.IfError(error => throw new InvalidOperationException($"it failed with error '{error}'"));
}

public static class ResultAssertionExtensions
{
    extension<T>(IAssertionSource<Result<T>> source)
    {
        public ResultIsSuccessAssertion<T> IsSuccess()
        {
            source.Context.ExpressionBuilder.Append($".IsSuccess()");
            return new(source.Context);
        }
    }
}