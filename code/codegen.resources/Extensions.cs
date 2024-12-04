using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace codegen.resources;

public static class StringModule
{
    private static readonly string[] lineSplitStrings = ["\r\n", "\r", "\n"];

    public static string CommaSeparate(this IEnumerable<string> input) =>
        input.Join(", ");

    public static string Join(this IEnumerable<string> input, string separator) =>
        string.Join(separator, input);

    public static string WithMaxBlankLines(this string input, int maxBlankLines) =>
        input.Split(lineSplitStrings, StringSplitOptions.None)
             .Aggregate((builder: new StringBuilder(), blankLineCount: 0),
                        (x, line) =>
                        {
                            var (builder, blankLineCount) = x;
                            if (string.IsNullOrWhiteSpace(line))
                            {
                                if (blankLineCount < maxBlankLines)
                                {
                                    builder.AppendLine(line);
                                    return (builder, blankLineCount + 1);
                                }
                                else
                                {
                                    return x;
                                }
                            }
                            else
                            {
                                builder.AppendLine(line);
                                return (builder, blankLineCount: 0);
                            }
                        },
                        acc => acc.builder.ToString().TrimEnd());

    public static string RemoveExtraneousLinesFromCode(this string input)
    {
        var splitLines = input.Split(lineSplitStrings, StringSplitOptions.None);

        return splitLines.Select((current, index) => (previous: index > 0 ? splitLines[index - 1] : null,
                                                      current,
                                                      next: index < splitLines.Length - 1 ? splitLines[index + 1] : null))
                         .Aggregate(new StringBuilder(),
                                    (builder, x) => (x.previous?.Trim(), x.current.Trim(), x.next?.Trim()) switch
                                    {
                                        // If the previous line is an open bracket and the current line is blank, skip it
                                        ("{", "", _) => builder,
                                        // If the current line is blank and the next line is a closing bracket, skip it
                                        (_, "", "}") => builder,
                                        // If both the current and next lines are blank, skip the current line
                                        (_, "", "") => builder,
                                        // Otherwise, add the line
                                        _ => builder.AppendLine(x.current)
                                    })
                         .ToString();
    }

    public static string FirstLetterToLowerCase(string text) =>
        text switch
        {
        [] => string.Empty,
        [var c, .. var rest] => new string([char.ToLowerInvariant(c), .. rest])
        };
}

public static class IEnumerableExtensions
{
    public static IEnumerable<T2> Choose<T, T2>(this IEnumerable<T> enumerable, Func<T, T2?> selector) =>
        from item in enumerable
        let result = selector(item)
        where result is not null
        select result;
}