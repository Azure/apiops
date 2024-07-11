using common;
using LanguageExt;
using LibGit2Sharp;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace publisher;

public sealed record CommitId : NonEmptyString
{
    public CommitId(string value) : base(value) { }
}

public static class Git
{
    public static FrozenSet<FileInfo> GetChangedFilesInCommit(DirectoryInfo directory, CommitId commitId)
    {
        var repositoryDirectory = GetRepositoryDirectory(directory);
        using var repository = new Repository(repositoryDirectory.FullName);

        return GetChanges(repository, commitId)
                .SelectMany(change => (change.Path, change.OldPath) switch
                {
                    (null, not null) => [change.OldPath],
                    (not null, null) => [change.Path],
                    (null, null) => [],
                    (var path, var oldPath) => new[] { path, oldPath }.Distinct()
                })
                .Select(path => new FileInfo(Path.Combine(repositoryDirectory.FullName, path)))
                
                .ToFrozenSet(x => x.FullName);
    }

    private static TreeChanges GetChanges(Repository repository, CommitId commitId)
    {
        var commit = GetCommit(repository, commitId);

        var parentCommit = commit.Parents.FirstOrDefault();

        return repository.Diff
                         .Compare<TreeChanges>(parentCommit?.Tree, commit.Tree);
    }

    private static DirectoryInfo GetRepositoryDirectory(DirectoryInfo directory)
    {
        var repositoryDirectory = directory.EnumerateDirectories(".git", SearchOption.TopDirectoryOnly)
                                           .FirstOrDefault();

        if (repositoryDirectory is not null)
        {
            return directory;
        }

        var parentDirectory = directory.Parent;

        return parentDirectory is null
                ? throw new InvalidOperationException("Could not find a Git repository.")
                : GetRepositoryDirectory(parentDirectory);
    }

    private static Commit GetCommit(Repository repository, CommitId commitId) =>
        repository.Commits
                  .Where(commit => commit.Id.Sha == commitId.Value)
                  .HeadOrNone()
                  .IfNone(() => throw new InvalidOperationException($"Could not find commit with ID {commitId.Value}."));

    public static Option<CommitId> TryGetPreviousCommitId(DirectoryInfo directory, CommitId commitId)
    {
        var repositoryDirectory = GetRepositoryDirectory(directory);
        using var repository = new Repository(repositoryDirectory.FullName);

        var commit = GetCommit(repository, commitId);

        return commit.Parents
                     .HeadOrNone()
                     .Map(parent => new CommitId(parent.Id.Sha));
    }

    public static Option<Stream> TryGetFileContentsInCommit(DirectoryInfo directory, FileInfo file, CommitId commitId)
    {
        var repositoryDirectory = GetRepositoryDirectory(directory);
        using var repository = new Repository(repositoryDirectory.FullName);

        var relativePath = Path.GetRelativePath(repositoryDirectory.FullName, file.FullName);
        var relativePathString = Path.DirectorySeparatorChar == '\\'
                                    ? relativePath.Replace('\\', '/')
                                    : relativePath;

        var blob = repository.Lookup<Blob>($"{commitId.Value}:{relativePathString}");

        return blob is null
                ? Option<Stream>.None
                : blob.GetContentStream();
    }

    public static FrozenSet<FileInfo> GetExistingFilesInCommit(DirectoryInfo directory, CommitId commitId)
    {
        var repositoryDirectory = GetRepositoryDirectory(directory);
        using var repository = new Repository(repositoryDirectory.FullName);

        var commit = GetCommit(repository, commitId);

        return commit.Tree
                     .SelectMany(treeEntry => GetFilesFromTreeEntry(treeEntry, repositoryDirectory))
                     .ToFrozenSet(x => x.FullName);
    }

    private static IEnumerable<FileInfo> GetFilesFromTreeEntry(TreeEntry entry, DirectoryInfo repositoryDirectory) =>
        entry.Target switch
        {
            Blob blob => [new FileInfo(Path.Combine(repositoryDirectory.FullName, entry.Path))],
            Tree tree => tree.SelectMany(child => GetFilesFromTreeEntry(child, repositoryDirectory)),
            _ => []
        };

    public static void InitializeRepository(DirectoryInfo directory, string commitMessage, string authorName, string authorEmail, DateTimeOffset signatureDate)
    {
        Repository.Init(directory.FullName);
        CommitChanges(directory, commitMessage, authorName, authorEmail, signatureDate);
    }

    public static Commit CommitChanges(DirectoryInfo directory, string commitMessage, string authorName, string authorEmail, DateTimeOffset signatureDate)
    {
        using var repository = new Repository(directory.FullName);
        Commands.Stage(repository, "*");
        repository.Index.Write();

        var author = new Signature(authorName, authorEmail, signatureDate);

        return repository.Commit(commitMessage, author, author);
    }
}
