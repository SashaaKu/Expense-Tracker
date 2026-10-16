using System.Threading.Channels;

namespace ExpenseTracker.Api.Categorization;

/// <summary>
/// Signals the background worker that new transactions may need categorizing.
/// Single slot: repeated requests while a run is pending collapse into one run.
/// </summary>
public sealed class CategorizationQueue
{
    private readonly Channel<bool> _signals = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Request() => _signals.Writer.TryWrite(true);

    internal ChannelReader<bool> Reader => _signals.Reader;
}

/// <summary>Runs categorization at startup and whenever a sync requests it, one run at a time.</summary>
public sealed class CategorizationWorker(
    CategorizationQueue queue, IServiceScopeFactory scopes, ILogger<CategorizationWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        queue.Request(); // pick up anything left uncategorized from earlier runs

        await foreach (var _ in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<CategorizationService>();
                await service.CategorizePendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (ClaudeCliException ex) when (ex.IsPermanent)
            {
                log.LogError("Categorization stopped, no retry until the next sync: {Reason}", ex.Message);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Categorization run failed");
            }
        }
    }
}
