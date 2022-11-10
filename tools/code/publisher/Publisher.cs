using common;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace publisher;

internal class Publisher : BackgroundService
{
    internal record Parameters
    {
        public required IHostApplicationLifetime ApplicationLifetime { get; init; }
        public CommitId? CommitId { get; init; }
        public FileInfo? ConfigurationFile { get; init; }
        public required JsonObject ConfigurationJson { get; init; }
        public required DeleteRestResource DeleteRestResource { get; init; }
        public required ILogger Logger { get; init; }
        public required ListRestResources ListRestResources { get; init; }
        public required PutRestResource PutRestResource { get; init; }
        public required ServiceDirectory ServiceDirectory { get; init; }
        public required ServiceUri ServiceUri { get; init; }
    }

    private readonly Parameters publisherParameters;

    public Publisher(Parameters publisherParameters)
    {
        this.publisherParameters = publisherParameters;
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var logger = publisherParameters.Logger;

        try
        {
            logger.LogInformation("Beginning execution...");

            await Run(cancellationToken);

            logger.LogInformation("Execution complete.");
        }
        catch (OperationCanceledException)
        {
            // Don't throw if operation was canceled
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "");
            Environment.ExitCode = -1;
            throw;
        }
        finally
        {
            publisherParameters.ApplicationLifetime.StopApplication();
        }
    }

    private async ValueTask Run(CancellationToken cancellationToken)
    {
        await (publisherParameters.CommitId is null
                ? RunWithoutCommitId(cancellationToken)
                : RunWithCommitId(publisherParameters.CommitId, cancellationToken));
    }

    private async ValueTask RunWithoutCommitId(CancellationToken cancellationToken)
    {
        var logger = publisherParameters.Logger;

        logger.LogInformation("Commit ID was not specified, will put all artifact files...");

        var files = publisherParameters.ServiceDirectory
                                       .EnumerateFilesRecursively()
                                       .ToImmutableList();

        await Service.ProcessArtifactsToPut(files,
                                            publisherParameters.ConfigurationJson,
                                            publisherParameters.ServiceDirectory,
                                            publisherParameters.ServiceUri,
                                            publisherParameters.ListRestResources,
                                            publisherParameters.PutRestResource,
                                            publisherParameters.DeleteRestResource,
                                            logger,
                                            cancellationToken);
    }

    private async ValueTask RunWithCommitId(CommitId commitId, CancellationToken cancellationToken)
    {
        var logger = publisherParameters.Logger;

        logger.LogInformation("Getting files from commit ID {commitId}...", commitId);
        var fileDictionary = await GetCommitIdFiles(commitId);

        if (fileDictionary.TryGetValue(Action.Delete, out var deletedFiles) && deletedFiles.Any())
        {
            logger.LogInformation("Processing files deleted in commit ID...");
            await ProcessDeletedCommitIdFiles(deletedFiles, cancellationToken);
        }

        if (fileDictionary.TryGetValue(Action.Put, out var putFiles) && putFiles.Any())
        {
            logger.LogInformation("Processing modified files in commit ID...");
            await ProcessCommitIdFilesToPut(putFiles, cancellationToken);
        }
    }

    private async ValueTask<ImmutableDictionary<Action, ImmutableList<FileInfo>>> GetCommitIdFiles(CommitId commitId)
    {
        var serviceDirectoryInfo = publisherParameters.ServiceDirectory.GetDirectoryInfo();
        var commitDictionary = await Git.GetFilesFromCommit(commitId, serviceDirectoryInfo);

        return commitDictionary.ToImmutableDictionary(kvp => MatchCommitStatusToAction(kvp.Key),
                                                      kvp => kvp.Value);
    }

    private static Action MatchCommitStatusToAction(CommitStatus commitStatus) =>
        commitStatus == CommitStatus.Delete
        ? Action.Delete
        : Action.Put;

    private async ValueTask ProcessDeletedCommitIdFiles(IReadOnlyCollection<FileInfo> deletedCommitIdFiles, CancellationToken cancellationToken)
    {
        await Service.ProcessDeletedArtifacts(deletedCommitIdFiles,
                                              publisherParameters.ConfigurationJson,
                                              publisherParameters.ServiceDirectory,
                                              publisherParameters.ServiceUri,
                                              publisherParameters.ListRestResources,
                                              publisherParameters.PutRestResource,
                                              publisherParameters.DeleteRestResource,
                                              publisherParameters.Logger,
                                              cancellationToken);
    }

    private async ValueTask ProcessCommitIdFilesToPut(IReadOnlyCollection<FileInfo> commitIdFilesToPut, CancellationToken cancellationToken)
    {
        await Service.ProcessArtifactsToPut(commitIdFilesToPut,
                                            publisherParameters.ConfigurationJson,
                                            publisherParameters.ServiceDirectory,
                                            publisherParameters.ServiceUri,
                                            publisherParameters.ListRestResources,
                                            publisherParameters.PutRestResource,
                                            publisherParameters.DeleteRestResource,
                                            publisherParameters.Logger,
                                            cancellationToken);
    }

    private enum Action
    {
        Put,
        Delete
    }
}