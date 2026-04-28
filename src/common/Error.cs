using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

namespace common;

#pragma warning disable CA1716 // Identifiers should not match keywords
/// <summary>
/// Represents an error containing messages and/or exceptions. Messages are case-insensitive and deduplicated.
/// </summary>
public sealed record Error
#pragma warning restore CA1716 // Identifiers should not match keywords
{
    /// <summary>
    /// Case-insensitive set of error messages.
    /// </summary>
    public ImmutableHashSet<string> Messages { get; }

    /// <summary>
    /// Set of all exceptions contained in this error.
    /// </summary>
    public ImmutableHashSet<Exception> Exceptions { get; }

    private Error(IEnumerable<string> messages, IEnumerable<Exception> exceptions)
    {
        Messages = messages.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        Exceptions = [.. exceptions];

        if (Messages.Count == 0 && Exceptions.Count == 0)
        {
            throw new ArgumentException("Error must have at least one message or exception.");
        }
    }

    /// <summary>
    /// Creates an error from one or more messages.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when no messages are provided.</exception>
    public static Error From(params string[] messages) =>
        messages.Length > 0
            ? new(messages, [])
            : throw new ArgumentException("At least one message required.", nameof(messages));

    /// <summary>
    /// Creates an error from one or more exceptions.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when no exceptions are provided.</exception>
    public static Error From(params Exception[] exceptions) =>
        exceptions.Length > 0
            ? new([], exceptions)
            : throw new ArgumentException("At least one exception required.", nameof(exceptions));

    /// <summary>
    /// Converts the error to an exception.
    /// </summary>
    /// <returns>
    /// A single exception is returned as-is. Multiple exceptions are wrapped in an <see cref="AggregateException"/>.
    /// Messages are converted to <see cref="InvalidOperationException"/> instances.
    /// </returns>
    public Exception ToException() =>
        (Messages.Count, Exceptions.Count) switch
        {
            (0, 1) => Exceptions.First(),
            (0, _) => new AggregateException(Exceptions),
            (1, 0) => new InvalidOperationException(Messages.First()),
            _ => new AggregateException([.. Messages.Select(message => new InvalidOperationException(message)),
                                         .. Exceptions])
        };

    /// <summary>
    /// Returns a string representation of the error, listing all messages and exceptions.
    /// </summary>
    public override string ToString()
    {
        var builder = new StringBuilder();

        Messages.Order().Iter(message => builder.AppendLine(message));

        Exceptions.Select(exception => $"{exception.GetType().Name}: {exception.Message}")
                  .Order()
                  .Iter(text => builder.AppendLine(text)); 

        return builder.ToString().Trim();
    }

    /// <summary>
    /// Converts a message to an <see cref="Error"./>.
    /// </summary>
    public static implicit operator Error(string message) =>
        From(message);

    /// <summary>
    /// Converts an exception to an <see cref="Error"/>.
    /// </summary>
    public static implicit operator Error(Exception exception) =>
        From(exception);

    /// <summary>
    /// Combines two errors.
    /// </summary>
    public static Error operator +(Error left, Error right) =>
        new([.. left.Messages, .. right.Messages],
            [.. left.Exceptions, .. right.Exceptions]);

    public bool Equals(Error? other) =>
        Messages.SetEquals(other?.Messages ?? [])
        && Exceptions.SetEquals(other?.Exceptions ?? []);

    public override int GetHashCode() =>
        HashCode.Combine(Messages.Aggregate(Messages.Count, (hash, message) => hash ^ StringComparer.OrdinalIgnoreCase.GetHashCode(message)),
                         Exceptions.Aggregate(Exceptions.Count, (hash, exception) => hash ^ exception.GetHashCode()));
}