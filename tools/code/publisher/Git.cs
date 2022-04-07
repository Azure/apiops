using common;
using Medallion.Shell;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace publisher;

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
        var gitRootDirectoryPath = await GetGitRootDirectoryPath(baseDirectory);
        var relativePath = Path.GetRelativePath(gitRootDirectoryPath, file.FullName);
        var command = Command.Run("git", "-C", gitRootDirectoryPath, "show", $"{commitId}^1:{relativePath}");
        var commandResult = await command.Task;

        return commandResult.Success
            ? commandResult.StandardOutput
            : throw new InvalidOperationException($"Failed to get contents for file {file} in Git commit {commitId}. Error message is '{commandResult.StandardError}'.");
    }

    public static async IAsyncEnumerable<IGrouping<CommitStatus, FileInfo>> GetFilesFromCommit(CommitId commitId, DirectoryInfo baseDirectory)
    {
        var diffTreeOutput = await GetDiffTreeOutput(commitId, baseDirectory);

        foreach (var grouping in ParseDiffTreeOutput(diffTreeOutput, baseDirectory))
        {
            yield return grouping;
        }
    }

    private static async Task<string> GetGitRootDirectoryPath(DirectoryInfo directory)
    {
        var command = Command.Run("git", "-C", directory.FullName, "rev-parse", "--show-toplevel");
        var commandResult = await command.Task;

        return commandResult.Success
            ? commandResult.StandardOutput.Trim()
            : throw new InvalidOperationException($"Failed to get root Git directory for {directory.FullName}. Error message is '{commandResult.StandardError}'.");
    }

    private static async Task<string> GetDiffTreeOutput(CommitId commitId, DirectoryInfo baseDirectory)
    {
        var command = Command.Run("git", "-C", baseDirectory.FullName, "diff-tree", "--no-commit-id", "--name-status", "--relative", "-r", $"{commitId}^", $"{commitId}");
        var commandResult = await command.Task;

        return commandResult.Success
            ? commandResult.StandardOutput
            : throw new InvalidOperationException($"Failed to get files for commit {commitId} in directory {baseDirectory}. Error message is '{commandResult.StandardError}'.");
    }

    private static IEnumerable<IGrouping<CommitStatus, FileInfo>> ParseDiffTreeOutput(string output, DirectoryInfo baseDirectory)
    {
        var getFileFromOutputLine = (string outputLine) => new FileInfo(Path.Combine(baseDirectory.FullName, outputLine[1..].Trim()));

        return
            from outputLine in output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            let commitStatus = TryGetCommitStatusFromOutputLine(outputLine)
            where commitStatus is not null
            let nonNullCommitStatus = commitStatus ?? throw new NullReferenceException() // Shouldn't be null here, adding to satisfy nullable compiler check
            let file = getFileFromOutputLine(outputLine)
            group file by nonNullCommitStatus;
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
