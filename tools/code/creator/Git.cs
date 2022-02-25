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

    public static Task<ILookup<CommitStatus, FileInfo>> GetFilesFromCommit(CommitId commitId, DirectoryInfo baseDirectory)
    {
        return GetDiffTreeOutput(commitId, baseDirectory).Map(output => ParseDiffTreeOutput(output, baseDirectory));
    }

    private static async Task<string> GetDiffTreeOutput(CommitId commitId, DirectoryInfo baseDirectory)
    {
        var command = Command.Run("git", "-C", baseDirectory.FullName, "diff-tree", "--no-commit-id", "--name-status", "--relative", "-r", $"{commitId}^", $"{commitId}");
        var commandResult = await command.Task;

        return commandResult.Success
            ? commandResult.StandardOutput
            : throw new InvalidOperationException($"Failed to get files for commit {commitId} in directory {baseDirectory}. Error message is '{commandResult.StandardError}'.");
    }

    private static ILookup<CommitStatus, FileInfo> ParseDiffTreeOutput(string output, DirectoryInfo baseDirectory)
    {
        var getFileFromOutputLine = (string outputLine) => new FileInfo(Path.Combine(baseDirectory.FullName, outputLine[1..].Trim()));

        return output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                     .ToLookup(TryGetCommitStatusFromOutputLine, getFileFromOutputLine)
                     .RemoveNullKeys();
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
