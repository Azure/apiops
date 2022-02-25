namespace common;

public abstract class ConsoleService : IHostedService
{
    private readonly CancellationTokenSource cancellationTokenSource = new();
    private readonly IHostApplicationLifetime applicationLifetime;

    protected ILogger Logger { get; }

    public ConsoleService(IHostApplicationLifetime applicationLifetime, ILogger logger)
    {
        this.applicationLifetime = applicationLifetime;
        Logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        applicationLifetime.ApplicationStarted.Register(async () =>
        {
            try
            {
                Logger.LogInformation("Beginning execution...");
                await ExecuteAsync(cancellationTokenSource.Token).ConfigureAwait(false);
                Logger.LogInformation("Execution complete.");
            }
            catch (Exception exception)
            {
                Logger.LogCritical(exception, exception.Message);
                Environment.ExitCode = -1;
                
                throw;
            }
            finally
            {
                applicationLifetime.StopApplication();
            }
        });

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationTokenSource.Cancel();

        return Task.CompletedTask;
    }

    protected abstract Task ExecuteAsync(CancellationToken cancellationToken);
}
