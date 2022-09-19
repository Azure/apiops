using common;
using Medallion.Shell;
using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace publisher;

internal record CommitId
{
    private readonly string value;

    public CommitId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Commit ID cannot be null or whitespace.", nameof(value));
        }

        this.value = value;
    }

    public override string ToString() => value;
}

internal enum CommitStatus
{
    Add,
    Copy,
    Delete,
    Modify,
    Rename,
    ChangeType,
    Unmerge,
    Unknown,
    Broken
}

internal static class Git
{
    public static async ValueTask<ImmutableDictionary<CommitStatus, ImmutableList<FileInfo>>> GetFilesFromCommit(CommitId commitId, DirectoryInfo baseDirectory)
    {
        var diffTreeOutput = await GetDiffTreeOutput(commitId, baseDirectory);

        return ParseDiffTreeOutput(diffTreeOutput, baseDirectory);
    }

    private static async ValueTask<string> GetDiffTreeOutput(CommitId commitId, DirectoryInfo baseDirectory)
    {
        var command = Command.Run("git", "-C", baseDirectory.FullName, "diff-tree", "--no-commit-id", "--name-status", "--relative", "-r", $"{commitId}^", $"{commitId}");
        var commandResult = await command.Task;

        return commandResult.Success
            ? commandResult.StandardOutput
            : throw new InvalidOperationException($"Failed to get files for commit {commitId} in directory {baseDirectory}. Error message is '{commandResult.StandardError}'.");
    }

    private static ImmutableDictionary<CommitStatus, ImmutableList<FileInfo>> ParseDiffTreeOutput(string output, DirectoryInfo baseDirectory)
    {
        return output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                     .Choose<string, (CommitStatus Status, FileInfo File)>(line =>
                     {
                         var commitStatus = TryGetCommitStatusFromOutputLine(line);
                         if (commitStatus is null)
                         {
                             return default;
                         }
                         else
                         {
                             var file = GetFileFromOutputLine(line, baseDirectory);
                             return (commitStatus.Value, file);
                         }
                     })
                     .GroupBy(pair => pair.Status, pair => pair.File)
                     .ToImmutableDictionary(grouping => grouping.Key, grouping => grouping.ToImmutableList());
    }

    private static CommitStatus? TryGetCommitStatusFromOutputLine(string diffTreeOutputLine)
    {
        return diffTreeOutputLine.ToUpper()[0] switch
        {
            'A' => CommitStatus.Add,
            'C' => CommitStatus.Copy,
            'D' => CommitStatus.Delete,
            'M' => CommitStatus.Modify,
            'R' => CommitStatus.Rename,
            'T' => CommitStatus.ChangeType,
            'U' => CommitStatus.Unmerge,
            'X' => CommitStatus.Unknown,
            'B' => CommitStatus.Broken,
            _ => null
        };
    }

    private static FileInfo GetFileFromOutputLine(string outputLine, DirectoryInfo baseDirectory)
    {
        var outputLinePath = outputLine[1..].Trim();
        var filePath = Path.Combine(baseDirectory.FullName, outputLinePath);
        return new FileInfo(filePath);
    }
}
