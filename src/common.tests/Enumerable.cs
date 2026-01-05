using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using TUnit.Assertions.Conditions;
using TUnit.Assertions.Conditions.Helpers;
using TUnit.Assertions.Core;

namespace common.tests;

public class SetEqualsAssertion<TCollection, TItem> : CollectionComparerBasedAssertion<TCollection, TItem>
    where TCollection : IEnumerable<TItem>
{
    private readonly IEnumerable<TItem> other;
    private readonly Func<TItem?, TItem?, bool>? equalityPredicate;

    [RequiresUnreferencedCode("Collection equivalency uses structural comparison for complex objects, which requires reflection and is not compatible with AOT")]
    public SetEqualsAssertion(
        AssertionContext<TCollection> context,
        IEnumerable<TItem> other,
        Func<TItem?, TItem?, bool>? equalityPredicate = default)
        : this(context, other, StructuralEqualityComparer<TItem>.Instance)
    {
        this.equalityPredicate = equalityPredicate;
    }

    public SetEqualsAssertion(
        AssertionContext<TCollection> context,
        IEnumerable<TItem> other,
        IEqualityComparer<TItem> comparer)
        : base(context)
    {
        this.other = other ?? throw new ArgumentNullException(nameof(other));
        Comparer = comparer;
    }

    public SetEqualsAssertion<TCollection, TItem> Using(IEqualityComparer<TItem> comparer)
    {
        SetComparer(comparer);
        return this;
    }

    public SetEqualsAssertion<TCollection, TItem> Using(Func<TItem?, TItem?, bool> equalityPredicate)
    {
        var comparer = EqualityComparer<TItem>.Create(equalityPredicate, _ => 0);
        SetComparer(comparer);
        return this;
    }

    protected override async Task<AssertionResult> CheckAsync(EvaluationMetadata<TCollection> metadata)
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

        var valueSet = ImmutableHashSet.CreateRange(value);
        var otherSet = ImmutableHashSet.CreateRange(other);
        if (equalityPredicate is not null)
        {
            var comparer = EqualityComparer<TItem>.Create(equalityPredicate, _ => 0);
            valueSet = valueSet.WithComparer(comparer);
            otherSet = otherSet.WithComparer(comparer);
        }

        if (valueSet.SetEquals(otherSet))
        {
            return AssertionResult.Passed;
        }
        else
        {
            var valueOnly = valueSet.Except(otherSet);
            var otherOnly = otherSet.Except(valueSet);

            return AssertionResult.Failed($"collections are not set-equal. [{string.Join(", ", valueOnly)}] only exist in this collection, while [{string.Join(", ", otherOnly)}] only exist in the other collection.");
        }
    }

    protected override string GetExpectation() =>
        $"to set-equal [{string.Join(", ", other)}]";
}

public class IntersectsWithAssertion<TCollection, TItem> : CollectionComparerBasedAssertion<TCollection, TItem>
    where TCollection : IEnumerable<TItem>
{
    private readonly IEnumerable<TItem> other;

    [RequiresUnreferencedCode("Collection equivalency uses structural comparison for complex objects, which requires reflection and is not compatible with AOT")]
    public IntersectsWithAssertion(
        AssertionContext<TCollection> context,
        IEnumerable<TItem> other)
        : this(context, other, StructuralEqualityComparer<TItem>.Instance)
    {
    }

    public IntersectsWithAssertion(
        AssertionContext<TCollection> context,
        IEnumerable<TItem> other,
        IEqualityComparer<TItem> comparer)
        : base(context)
    {
        this.other = other ?? throw new ArgumentNullException(nameof(other));
        Comparer = comparer;
    }

    public IntersectsWithAssertion<TCollection, TItem> Using(IEqualityComparer<TItem> comparer)
    {
        SetComparer(comparer);
        return this;
    }

    public IntersectsWithAssertion<TCollection, TItem> Using(Func<TItem?, TItem?, bool> equalityPredicate)
    {
        var comparer = EqualityComparer<TItem>.Create(equalityPredicate, _ => 0);
        SetComparer(comparer);
        return this;
    }

    protected override async Task<AssertionResult> CheckAsync(EvaluationMetadata<TCollection> metadata)
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
        else if (value.Intersect(other, GetComparer()).Any())
        {
            return AssertionResult.Passed;
        }
        else
        {
            return AssertionResult.Failed("collections do not intersect");
        }
    }

    protected override string GetExpectation() =>
        $"to intersect with [{string.Join(", ", other)}]";
}

public class DoesNotIntersectWithAssertion<TCollection, TItem> : CollectionComparerBasedAssertion<TCollection, TItem>
    where TCollection : IEnumerable<TItem>
{
    private readonly IEnumerable<TItem> other;

    [RequiresUnreferencedCode("Collection equivalency uses structural comparison for complex objects, which requires reflection and is not compatible with AOT")]
    public DoesNotIntersectWithAssertion(
        AssertionContext<TCollection> context,
        IEnumerable<TItem> other)
        : this(context, other, StructuralEqualityComparer<TItem>.Instance)
    {
    }

    public DoesNotIntersectWithAssertion(
        AssertionContext<TCollection> context,
        IEnumerable<TItem> other,
        IEqualityComparer<TItem> comparer)
        : base(context)
    {
        this.other = other ?? throw new ArgumentNullException(nameof(other));
        Comparer = comparer;
    }

    public DoesNotIntersectWithAssertion<TCollection, TItem> Using(IEqualityComparer<TItem> comparer)
    {
        SetComparer(comparer);
        return this;
    }

    public DoesNotIntersectWithAssertion<TCollection, TItem> Using(Func<TItem?, TItem?, bool> equalityPredicate)
    {
        var comparer = EqualityComparer<TItem>.Create(equalityPredicate, _ => 0);
        SetComparer(comparer);
        return this;
    }

    protected override async Task<AssertionResult> CheckAsync(EvaluationMetadata<TCollection> metadata)
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
        else if (ReferenceEquals(value, other))
        {
            return AssertionResult.Failed("collections reference the same object.");
        }
        ;

        var firstIntersections = value.Intersect(other, GetComparer())
                                      .ToImmutableArray();

        if (firstIntersections.Length > 0)
        {
            return AssertionResult.Failed($"collections intersect on the following items: [{string.Join(", ", firstIntersections)}]");
        }
        else
        {
            return AssertionResult.Passed;
        }
    }

    protected override string GetExpectation() =>
        $"to not intersect with [{string.Join(", ", other)}]";
}

public class BeSubsetOfAssertion<TCollection, TItem> : CollectionComparerBasedAssertion<TCollection, TItem>
    where TCollection : IEnumerable<TItem>
{
    private readonly IEnumerable<TItem> superset;

    [RequiresUnreferencedCode("Collection equivalency uses structural comparison for complex objects, which requires reflection and is not compatible with AOT")]
    public BeSubsetOfAssertion(
        AssertionContext<TCollection> context,
        IEnumerable<TItem> superset)
        : this(context, superset, StructuralEqualityComparer<TItem>.Instance)
    {
    }

    public BeSubsetOfAssertion(
        AssertionContext<TCollection> context,
        IEnumerable<TItem> superset,
        IEqualityComparer<TItem> comparer)
        : base(context)
    {
        this.superset = superset ?? throw new ArgumentNullException(nameof(superset));
        Comparer = comparer;
    }

    public BeSubsetOfAssertion<TCollection, TItem> Using(IEqualityComparer<TItem> comparer)
    {
        SetComparer(comparer);
        return this;
    }

    public BeSubsetOfAssertion<TCollection, TItem> Using(Func<TItem?, TItem?, bool> equalityPredicate)
    {
        var comparer = EqualityComparer<TItem>.Create(equalityPredicate, _ => 0);
        SetComparer(comparer);
        return this;
    }

    protected override async Task<AssertionResult> CheckAsync(EvaluationMetadata<TCollection> metadata)
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

        var missingItems = value.Except(superset, GetComparer()).ToImmutableArray();
        if (missingItems.Any())
        {
            return AssertionResult.Failed($"collections are missing the following items: [{string.Join(", ", missingItems)}]");
        }
        else
        {
            return AssertionResult.Passed;
        }
    }

    protected override string GetExpectation() =>
        $"to be subset of [{string.Join(", ", superset)}]";
}

public static class CollectionAssertionExtensions
{
#pragma warning disable CA1305 // Specify IFormatProvider
    extension<TCollection, TItem>(IAssertionSource<TCollection> source) where TCollection : IEnumerable<TItem>
    {
        public SetEqualsAssertion<TCollection, TItem> SetEquals(IEnumerable<TItem> other)
        {
            source.Context.ExpressionBuilder.Append($".SetEquals({string.Join(", ", other)})");
            return new(source.Context, other);
        }

        public IntersectsWithAssertion<TCollection, TItem> IntersectsWith(IEnumerable<TItem> other)
        {
            source.Context.ExpressionBuilder.Append($".IntersectsWith({string.Join(", ", other)})");
            return new(source.Context, other);
        }

        public DoesNotIntersectWithAssertion<TCollection, TItem> DoesNotIntersectWith(IEnumerable<TItem> other)
        {
            source.Context.ExpressionBuilder.Append($".DoesNotIntersectWith({string.Join(", ", other)})");
            return new(source.Context, other);
        }

        public BeSubsetOfAssertion<TCollection, TItem> BeSubsetOf(IEnumerable<TItem> superset)
        {
            source.Context.ExpressionBuilder.Append($".BeSubsetOf({string.Join(", ", superset)})");
            return new(source.Context, superset);
        }
#pragma warning restore CA1305 // Specify IFormatProvider
    }
}