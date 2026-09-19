using Imperial2030.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace Imperial2030.Server.Data;

public static class GameQueries
{
    /// <summary>
    /// The game with everything an engine call may read or write: players (and their users, for names),
    /// nation states, bonds, units and territory states. One query shape for every caller that runs an
    /// engine, so no endpoint can leave out a collection the engine needs - an unloaded collection reads
    /// as empty, and a write to it creates duplicate rows (see <c>MoveNationFlagRowsTests</c>). Split
    /// query per .agents/AGENTS.md rule #19. Actions are not loaded: the logger numbers a new entry from
    /// the database when they are not.
    /// </summary>
    public static Task<Game?> LoadGameGraphAsync(this ApplicationDbContext context, Guid gameId) => context.Games
        .Include(g => g.Players).ThenInclude(p => p.User)
        .Include(g => g.NationStates)
        .Include(g => g.Bonds)
        .Include(g => g.Units)
        .Include(g => g.TerritoryStates)
        .AsSplitQuery()
        .FirstOrDefaultAsync(g => g.Id == gameId);

    /// <summary>
    /// The game as <see cref="Helpers.GameDetailDtoBuilder"/> projects it: the graph above, plus the
    /// users behind every player reference (names) and the action log.
    /// </summary>
    public static Task<Game?> LoadGameDetailAsync(this ApplicationDbContext context, Guid gameId) => context.Games
        .Include(g => g.Players).ThenInclude(p => p.User)
        .Include(g => g.NationStates).ThenInclude(ns => ns.Controller).ThenInclude(c => c!.User)
        .Include(g => g.Bonds).ThenInclude(b => b.Holder).ThenInclude(h => h!.User)
        .Include(g => g.TerritoryStates)
        .Include(g => g.Units)
        .Include(g => g.Actions)
        .AsSplitQuery()
        .FirstOrDefaultAsync(g => g.Id == gameId);
}
