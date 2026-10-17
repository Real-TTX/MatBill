using MatBill.Data;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Services;

/// <summary>What was cleaned up: how many rows of each kind were removed.</summary>
public sealed record MaintenanceResult(int Sessions, int ActivityEntries, int BackupRuns, int ExpiredLinks, int Notifications = 0)
{
    public int Total => Sessions + ActivityEntries + BackupRuns + ExpiredLinks + Notifications;
}

/// <summary>
/// Keeps the tables that only grow small: sessions that ended long ago, share links that ran out long ago, and the logs after a year. Runs once a day (and on request
/// on the system page). It deletes with single statements and never touches invoices, customers or series.
/// </summary>
public sealed class MaintenanceService(IServiceScopeFactory scopes, ILogger<MaintenanceService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var result = await RunAsync(scope.ServiceProvider.GetRequiredService<MbDbContext>(), scope.ServiceProvider.GetRequiredService<TimeProvider>(), stoppingToken);
                    if (result.Total > 0)
                    {
                        logger.LogInformation("Maintenance removed {Total} old rows.", result.Total);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "The maintenance run failed.");
                }

                await Task.Delay(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // the application is stopping
        }
    }

    public static async Task<MaintenanceResult> RunAsync(MbDbContext db, TimeProvider clock, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var month = now.AddDays(-30);
        var day = now.AddDays(-1);
        var year = now.AddDays(-365);

        var sessions = await db.UserSession.Where(s => s.ExpiresDate < month || (s.RevokedDate != null && s.RevokedDate < month)).ExecuteDeleteAsync(ct);
        var activity = await db.ActivityLog.Where(a => a.OccurredDate < year).ExecuteDeleteAsync(ct);
        var runs = await db.BackupRun.Where(r => r.EndDate < year).ExecuteDeleteAsync(ct);
        var links = await db.ShareLink.Where(l => l.ExpiresDate < month || (l.RevokedDate != null && l.RevokedDate < month)).ExecuteDeleteAsync(ct);
        var notes = await db.Notification.Where(n => (n.ReadDate != null && n.ReadDate < now.AddDays(-90)) || n.OccurredDate < year).ExecuteDeleteAsync(ct);
        return new MaintenanceResult(sessions, activity, runs, links, notes);
    }
}
