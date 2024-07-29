using LanguageExt;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public static class OptionExtensions
{
    /// <summary>
    /// If <paramref name="option"/> is Some, execute <paramref name="action"/> on its value.
    /// </summary>
    public static async ValueTask IterTask<T>(this Option<T> option, Func<T, ValueTask> action) =>
        await option.IterTask((t, _) => action(t), CancellationToken.None);

    /// <summary>
    /// If <paramref name="option"/> is Some, execute <paramref name="action"/> on its value.
    /// </summary>
    public static async ValueTask IterTask<T>(this Option<T> option, Func<T, CancellationToken, ValueTask> action, CancellationToken cancellationToken) =>
        await option.Match(t => action(t, cancellationToken), () => ValueTask.CompletedTask);

    /// <summary>
    /// If <paramref name="option"/> is Some, apply <paramref name="map"/> to its value and return
    /// the <typeparamref name="T2"/> result wrapped in Some. Otherwise, return None of type <typeparamref name="T2"/>.
    /// </summary>
    public static async ValueTask<Option<T2>> MapTask<T, T2>(this Option<T> option, Func<T, ValueTask<T2>> map) =>
        await option.BindTask(async t =>
        {
            var t2 = await map(t);
            return Option<T2>.Some(t2);
        });

    /// <summary>
    /// If <paramref name="option"/> is Some, apply <paramref name="bind"/> to its value and return
    /// the result. Otherwise, return None of type <typeparamref name="T2"/>.
    /// </summary>
    public static async ValueTask<Option<T2>> BindTask<T, T2>(this Option<T> option, Func<T, ValueTask<Option<T2>>> bind) =>
        await option.BindTask((t, _) => bind(t), CancellationToken.None);

    /// <summary>
    /// If <paramref name="option"/> is Some, apply <paramref name="bind"/> to its value and return
    /// the result. Otherwise, return None of type <typeparamref name="T2"/>.
    /// </summary>
    public static async ValueTask<Option<T2>> BindTask<T, T2>(this Option<T> option, Func<T, CancellationToken, ValueTask<Option<T2>>> bind, CancellationToken cancellationToken) =>
        await option.Match(t => bind(t, cancellationToken), () => ValueTask.FromResult(Option<T2>.None));

    public static async ValueTask<Option<T>> Or<T>(this Option<T> option, Func<ValueTask<Option<T>>> alternative) =>
         await option.Match(t => ValueTask.FromResult(Option<T>.Some(t)), alternative);

    public static async ValueTask<Option<T>> Or<T>(this ValueTask<Option<T>> optionTask, Func<ValueTask<Option<T>>> alternative)
    {
        var option = await optionTask;
        return await option.Or(alternative);
    }
}