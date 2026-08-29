using Imperial2030.Server.Helpers;
using Imperial2030.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Imperial2030.Server.Data;

/// <summary>
/// One-off housekeeping run once per process start, after the database has been seeded/migrated.
///
/// Extracted from Program.cs, which had accumulated JWT resolution, rate-limit policies, Identity
/// lockout, a ReplaySessionManager factory and this data maintenance in one file. Composition and data
/// chores are different jobs; keeping them together made Program.cs the place unrelated policies met.
///
/// Deliberately not a hosted service: this must finish before the first request is served (the
/// WinnerName backfill is what the lobby reads), and IHostedService.StartAsync runs concurrently with
/// the server coming up. Calling it explicitly keeps the ordering obvious.
/// </summary>
public static class StartupMaintenance
{
    /// <summary>
    /// How long an unfinished game may sit untouched before it is deleted. Lobby and in-progress games
    /// only — finished games are the historical record and are never swept.
    /// </summary>
    public static readonly TimeSpan AbandonedGameRetention = TimeSpan.FromDays(14);

    /// <summary>
    /// Seeds/migrates, then runs the housekeeping passes. Failures are logged and swallowed: a stale-data
    /// chore must never stop the server from starting, and both passes are idempotent, so the next start
    /// simply retries.
    /// </summary>
    public static async Task RunAsync(IServiceProvider services, ILogger logger)
    {
        try
        {
            await DbSeeder.SeedAsync(services);

            var dbContext = services.GetRequiredService<ApplicationDbContext>();
            await RemoveAbandonedGamesAsync(dbContext, logger);
            await BackfillMissingWinnerNamesAsync(dbContext, logger);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Startup maintenance failed (database seed or housekeeping).");
        }
    }

    private static async Task RemoveAbandonedGamesAsync(ApplicationDbContext dbContext, ILogger logger)
    {
        var cutoff = DateTime.UtcNow - AbandonedGameRetention;
        var statuses = new[] { GameStatus.Lobby, GameStatus.InProgress };

        var abandoned = await dbContext.Games
            .Where(g => statuses.Contains(g.Status) && g.CreatedAt < cutoff)
            .ToListAsync();

        if (abandoned.Count == 0) return;

        dbContext.Games.RemoveRange(abandoned);
        await dbContext.SaveChangesAsync();
        logger.LogInformation(
            "Removed {Count} abandoned lobby/in-progress games created before {Cutoff:u}.",
            abandoned.Count, cutoff);
    }

    /// <summary>
    /// Fills in WinnerName for finished games saved before that column existed. Idempotent — once a game
    /// has a name it is no longer selected.
    /// </summary>
    private static async Task BackfillMissingWinnerNamesAsync(ApplicationDbContext dbContext, ILogger logger)
    {
        var missing = await dbContext.Games
            .Where(g => g.Status == GameStatus.Finished && (g.WinnerName == null || g.WinnerName == ""))
            .ToListAsync();

        if (missing.Count == 0) return;

        logger.LogInformation("Backfilling WinnerName for {Count} finished games.", missing.Count);
        foreach (var game in missing)
        {
            await game.SetWinnerNameAsync(dbContext);
        }
        await dbContext.SaveChangesAsync();
    }
}
