using LibGit2Sharp;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public sealed record CommitId
{
    private readonly string value;

    private CommitId(string value) => this.value = value;

    public static Result<CommitId> From(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? Error.From("Commit ID cannot be null or empty.")
            : new CommitId(value);

    public static CommitId From(Commit commit) =>
        From(commit.Id.Sha).IfErrorThrow();

    public override string ToString() => value;
}

public enum GitAction
{
    Put,
    Delete
}

public static class GitModule
{
    public static ImmutableHashSet<FileInfo> GetCommitFiles(CommitId commitId, DirectoryInfo directory)
    {
        var repositoryDirectory = GetRepositoryDirectory(directory);
        using var repository = new Repository(repositoryDirectory.FullName);

        var commit = GetCommit(repository, commitId);

        return commit.Tree
                     .SelectMany(entry => GetFilesFromTreeEntry(entry, repositoryDirectory))
                     .ToImmutableHashSet(FileInfoModule.Comparer);
    }

    private static DirectoryInfo GetRepositoryDirectory(DirectoryInfo directory)
    {
        DirectoryInfo? currentDirectory = directory;

        while (currentDirectory is not null)
        {
            var gitDirectory = currentDirectory.GetChildDirectory(".git");
            if (gitDirectory.Exists())
            {
                return currentDirectory;
            }

            currentDirectory = currentDirectory.Parent;
        }

        throw new DirectoryNotFoundException($"No Git repository found in {directory.FullName} or its parents.");
    }

    private static Commit GetCommit(Repository repository, CommitId commitId) =>
        repository.Lookup<Commit>(commitId.ToString())
            ?? throw new ArgumentException($"Commit with ID {commitId} not found in repository {repository.Info.WorkingDirectory}.");

    private static IEnumerable<FileInfo> GetFilesFromTreeEntry(TreeEntry entry, DirectoryInfo repositoryDirectory) =>
        entry.Target switch
        {
            Blob => [new FileInfo(Path.Combine(repositoryDirectory.FullName, entry.Path))],
            Tree tree => tree.SelectMany(child => GetFilesFromTreeEntry(child, repositoryDirectory)),
            _ => []
        };

    public static ImmutableDictionary<GitAction, ImmutableHashSet<FileInfo>> GetFilesModifiedByCommit(CommitId commitId, DirectoryInfo directory)
    {
        var repositoryDirectory = GetRepositoryDirectory(directory);
        using var repository = new Repository(repositoryDirectory.FullName);

        var commit = GetCommit(repository, commitId);

        return repository.Diff
                         .Compare<TreeChanges>(commit.Parents.FirstOrDefault()?.Tree, commit.Tree)
                         .SelectMany(change => change.Status switch
                         {
                             ChangeKind.Added => new[] { (Action: GitAction.Put, Path: change.Path) },
                             ChangeKind.Deleted => [(Action: GitAction.Delete, Path: change.OldPath)],
                             ChangeKind.Modified => [(Action: GitAction.Put, Path: change.Path)],
                             ChangeKind.Copied => [(Action: GitAction.Put, Path: change.Path)],
                             ChangeKind.Renamed =>
                             [
                                 (Action: GitAction.Put, Path: change.Path),
                                 (Action: GitAction.Delete, Path: change.OldPath)
                             ],
                             _ => []
                         })
                         .GroupBy(change => change.Action)
                         .ToImmutableDictionary(group => group.Key,
                                                group => group.Select(change => change.Path)
                                                              .Select(path => new FileInfo(Path.Combine(repositoryDirectory.FullName, path)))
                                                              .ToImmutableHashSet(FileInfoModule.Comparer));
    }

    public static Option<CommitId> GetPreviousCommitId(CommitId commitId, DirectoryInfo directory)
    {
        var repositoryDirectory = GetRepositoryDirectory(directory);
        using var repository = new Repository(repositoryDirectory.FullName);

        var commit = GetCommit(repository, commitId);

        return commit.Parents
                     .Head()
                     .Map(parent => CommitId.From(parent.Id.Sha)
                                            .IfErrorThrow());
    }

    public static async ValueTask<Option<BinaryData>> ReadFile(FileInfo file, CommitId commitId, CancellationToken cancellationToken)
    {
        var fileDirectory = file.Directory;
        if (fileDirectory is null)
        {
            return Option.None;
        }

        var repositoryDirectory = GetRepositoryDirectory(fileDirectory);
        using var repository = new Repository(repositoryDirectory.FullName);

        // Normalize path separators to Git's expected format
        var relativePath = Path.GetRelativePath(repositoryDirectory.FullName, file.FullName)
                               .Replace(Path.DirectorySeparatorChar, '/')
                               .Replace(Path.AltDirectorySeparatorChar, '/')
                               .Replace("//", "/")
                               .Trim('/');

        var blob = repository.Lookup<Blob>($"{commitId}:{relativePath}");
        if (blob is null)
        {
            return Option.None;
        }

        using var stream = blob.GetContentStream();

        return await BinaryData.FromStreamAsync(stream, cancellationToken);
    }

    public static Commit InitializeRepository(DirectoryInfo directory, string commitMessage, string authorName, string authorEmail, DateTimeOffset signatureDate)
    {
        Repository.Init(directory.FullName);
        return CommitChanges(directory, commitMessage, authorName, authorEmail, signatureDate);
    }

    public static Commit CommitChanges(DirectoryInfo directory, string commitMessage, string authorName, string authorEmail, DateTimeOffset signatureDate)
    {
        using var repository = new Repository(directory.FullName);
        Commands.Stage(repository, "*");
        repository.Index.Write();

        var author = new Signature(authorName, authorEmail, signatureDate);

        return repository.Commit(commitMessage, author, author);
    }

    public static Option<IEnumerable<DirectoryInfo>> GetSubDirectories(CommitId commitId, DirectoryInfo targetDirectory)
    {
        var repositoryDirectory = GetRepositoryDirectory(targetDirectory);
        using var repository = new Repository(repositoryDirectory.FullName);
        var commit = GetCommit(repository, commitId);

        // Get relative path from repository root to target directory
        var relativePath = Path.GetRelativePath(repositoryDirectory.FullName, targetDirectory.FullName)
                               .Replace(Path.DirectorySeparatorChar, '/')
                               .Replace(Path.AltDirectorySeparatorChar, '/')
                               .Replace("//", "/")
                               .Trim('/');

        var lookupPath = relativePath is "." || string.IsNullOrWhiteSpace(relativePath)
                            ? commitId.ToString()
                            : $"{commitId}:{relativePath}";

        var tree = repository.Lookup<Tree>(lookupPath);
        if (tree is null)
        {
            return Option.None;
        }

        var entries = from entry in tree
                      where entry.Mode == Mode.Directory
                      let path = Path.Combine(targetDirectory.FullName, entry.Name)
                      select new DirectoryInfo(path);

        return entries.ToImmutableArray();
    }
}
