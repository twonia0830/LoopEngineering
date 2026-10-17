using LoopEngineering.Core.Loop;

namespace LoopEngineering.Web;

/// <summary>
/// Refreshes the account quota on a fixed interval (Quota:RefreshSeconds, default 60) so the header
/// catches up with usage made elsewhere (VS Code, other tools) and with GitHub's own reporting delay.
/// </summary>
public sealed class QuotaRefreshWorker(AccountQuotaService quota, IConfiguration configuration, ILogger<QuotaRefreshWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = Math.Max(15, configuration.GetValue("Quota:RefreshSeconds", 60));
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
        logger.LogInformation("Quota refresh every {Seconds}s.", seconds);

        try
        {
            // First fetch right away, then on every tick. RefreshAsync never throws and no-ops without a token variable.
            do
            {
                await quota.RefreshAsync(force: true);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // app shutting down
        }
    }
}
