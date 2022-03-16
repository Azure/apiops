using common;
using Medallion.Shell;
using System;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace creator;

internal record CommitId : NonEmptyString
{
    private CommitId(string value) : base(value)
    {
    }

    public static CommitId From([NotNull] string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Commit ID cannot be null or whitespace.", nameof(value))
            : new CommitId(value);
    }
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
    public static async Task<string> GetPreviousCommitContents(CommitId commitId, FileInfo file, DirectoryInfo baseDirectory)
    {
        var relativePath = Path.GetRelativePath(baseDirectory.FullName, file.FullName);
        var command = Command.Run("git", "-C", baseDirectory.FullName, "show", $"{commitId}^1:{relativePath}");
        var commandResult = await command.Task;

        return commandResult.Success
            ? commandResult.StandardOutput
            : throw new InvalidOperationException($"Failed to get contents for file {file} in Git commit {commitId}. Error message is '{commandResult.StandardError}'.");
    }

    public static async Task<ImmutableDictionary<CommitStatus, ImmutableList<FileInfo>>> GetFilesFromCommit(CommitId commitId, DirectoryInfo baseDirectory)
    {
        var diffTreeOutput = await GetDiffTreeOutput(commitId, baseDirectory);

        return ParseDiffTreeOutput(diffTreeOutput, baseDirectory);
    }

    private static async Task<string> GetDiffTreeOutput(CommitId commitId, DirectoryInfo baseDirectory)
    {
        var command = Command.Run("git", "-C", baseDirectory.FullName, "diff-tree", "--no-commit-id", "--name-status", "--relative", "-r", $"{commitId}^", $"{commitId}");
        var commandResult = await command.Task;

        return commandResult.Success
            ? commandResult.StandardOutput
            : throw new InvalidOperationException($"Failed to get files for commit {commitId} in directory {baseDirectory}. Error message is '{commandResult.StandardError}'.");
    }

    private static ImmutableDictionary<CommitStatus, ImmutableList<FileInfo>> ParseDiffTreeOutput(string output, DirectoryInfo baseDirectory)
    {
        var getFileFromOutputLine = (string outputLine) => new FileInfo(Path.Combine(baseDirectory.FullName, outputLine[1..].Trim()));

        var commitStatusGroups =
            from outputLine in output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            let commitStatus = TryGetCommitStatusFromOutputLine(outputLine)
            where commitStatus is not null
            let nonNullCommitStatus = commitStatus ?? throw new NullReferenceException() // Shouldn't be null here, adding to satisfy nullable compiler check
            let file = getFileFromOutputLine(outputLine)
            group file by nonNullCommitStatus into commitStatusGroup
            select (CommitStatus: commitStatusGroup.Key, Files: commitStatusGroup.ToImmutableList());

        return commitStatusGroups.ToImmutableDictionary(group => group.CommitStatus, group => group.Files);
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
}
