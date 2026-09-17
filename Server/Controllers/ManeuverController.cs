using Imperial2030.Server.Data;
using Imperial2030.Server.Engine;
using Imperial2030.Server.Models;
using Imperial2030.Server.Helpers;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace Imperial2030.Server.Controllers;

[Route("api/[controller]")]
[ApiController]
// Every action here is a game move, so the whole controller refuses guests - GamesController makes the
// same refusal per-action because some of its endpoints are deliberately guest-readable.
[Authorize(Policy = GameConstants.NotGuestPolicy)]
public class ManeuverController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IHubContext<Imperial2030.Server.Hubs.GameHub> _hubContext;
    private readonly Imperial2030.Server.Services.BotService _botService;
    private readonly ILogger<ManeuverController> _logger;

    // logger is optional so the many direct `new ManeuverController(...)` constructions in Tests/ keep
    // working; DI supplies the real one in production.
    public ManeuverController(ApplicationDbContext context, IHubContext<Imperial2030.Server.Hubs.GameHub> hubContext, Imperial2030.Server.Services.BotService botService, ILogger<ManeuverController>? logger = null)
    {
        _context = context;
        _hubContext = hubContext;
        _botService = botService;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ManeuverController>.Instance;
    }

    private Task<Game?> LoadGame(Guid gameId) => _context.Games
        .Include(g => g.Units)
        .Include(g => g.NationStates)
        .Include(g => g.TerritoryStates)
        .Include(g => g.Players)
            .ThenInclude(p => p.User)
        .AsSplitQuery()
        .FirstOrDefaultAsync(g => g.Id == gameId);

    /// <summary>
    /// The caller must be the acting nation's government - checked before the engine runs, because it
    /// mutates. Null when the caller is; otherwise the response to return.
    /// </summary>
    private IActionResult? ForbidUnlessActiveGovernment(Game game, string userId)
    {
        var nationState = game.NationStates.First(n => n.Nation == game.CurrentTurnNation);
        var controller = game.Players.FirstOrDefault(p => p.Id == nationState.ControllerId);
        if (controller == null || controller.UserId != userId) return Forbid();
        return null;
    }

    private async Task Broadcast(Guid gameId)
    {
        await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameUpdated", gameId);
    }

    [HttpPost("{gameId}/move-fleet")]
    public async Task<IActionResult> MoveFleet(Guid gameId, [FromBody] MoveUnitRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var game = await LoadGame(gameId);
        if (game == null) return NotFound();
        if (ForbidUnlessActiveGovernment(game, userId) is { } forbidden) return forbidden;

        var result = ManeuverEngine.MoveFleet(_context, game, request.UnitId, request.DestinationId, request.IsHostile, request.BattleTargetNation, request.BattleTargetUnitType);
        if (!result.Ok) return result.Error == "Unit not found." ? NotFound(result.Error) : BadRequest(result.Error);

        // No flag placement per move: flags are step 3 of the maneuver, placed once every unit has
        // moved (p.8/p.10) - which is when the phase ends.
        if (!result.BattlePending && !result.Stayed) ManeuverEngine.TryAutoAdvanceManeuver(_context, game, game.CurrentTurnNation);

        await _context.SaveChangesAsync();
        await Broadcast(gameId);
        if (game.PendingBattleDefenders.Any()) _botService.TriggerBotTurn(gameId);
        return Ok();
    }

    [HttpPost("{gameId}/battle")]
    public async Task<IActionResult> Battle(Guid gameId, [FromBody] MoveUnitRequest request)
    {
        // Stationary Battle: the unit attacks a named nation's unit where it already stands.
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var game = await LoadGame(gameId);
        if (game == null) return NotFound();
        if (ForbidUnlessActiveGovernment(game, userId) is { } forbidden) return forbidden;
        if (!request.BattleTargetNation.HasValue) return BadRequest("Target nation required.");

        var result = ManeuverEngine.StationaryBattle(_context, game, request.UnitId, request.BattleTargetNation.Value);
        if (!result.Ok) return result.Error == "Unit not found." ? NotFound(result.Error) : BadRequest(result.Error);

        // Destroying the last unmoved unit ends the phase; flags are settled then (p.10, step 3).
        ManeuverEngine.TryAutoAdvanceManeuver(_context, game, game.CurrentTurnNation);

        await _context.SaveChangesAsync();
        await Broadcast(gameId);
        return Ok();
    }

    [HttpPost("{gameId}/move-army")]
    public async Task<IActionResult> MoveArmy(Guid gameId, [FromBody] MoveUnitRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var game = await LoadGame(gameId);
        if (game == null) return NotFound();
        if (ForbidUnlessActiveGovernment(game, userId) is { } forbidden) return forbidden;

        var result = ManeuverEngine.MoveArmy(_context, game, request.UnitId, request.DestinationId, request.IsHostile, request.ConvoyFleetIds, request.BattleTargetNation, request.BattleTargetUnitType);
        if (!result.Ok) return result.Error == "Unit not found." ? NotFound(result.Error) : BadRequest(result.Error);

        if (!result.BattlePending && !result.Stayed) ManeuverEngine.TryAutoAdvanceManeuver(_context, game, game.CurrentTurnNation);

        await _context.SaveChangesAsync();
        await Broadcast(gameId);
        if (game.PendingBattleDefenders.Any()) _botService.TriggerBotTurn(gameId);
        return Ok();
    }

    [HttpPost("{gameId}/toggle-hostility/{unitId}")]
    public async Task<IActionResult> ToggleHostility(Guid gameId, Guid unitId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var game = await LoadGame(gameId);
        if (game == null) return NotFound();
        if (ForbidUnlessActiveGovernment(game, userId) is { } forbidden) return forbidden;

        var result = ManeuverEngine.ToggleHostility(_context, game, unitId);
        if (!result.Ok) return result.Error == "Unit not found." ? NotFound(result.Error) : BadRequest(result.Error);

        await _context.SaveChangesAsync();
        await Broadcast(gameId);
        return Ok();
    }

    [HttpPost("{gameId}/destroy-factory")]
    public async Task<IActionResult> DestroyFactory(Guid gameId, [FromBody] DestroyFactoryRequest request)
    {
        var userId = HttpContext?.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var game = await LoadGame(gameId);
        if (game == null) return NotFound();
        if (ForbidUnlessActiveGovernment(game, userId) is { } forbidden) return forbidden;

        var result = ManeuverEngine.DestroyFactory(_context, game, request.TerritoryId, request.UnitIds);
        if (!result.Ok) return BadRequest(result.Error);

        ManeuverEngine.TryAutoAdvanceManeuver(_context, game, game.CurrentTurnNation);

        await _context.SaveChangesAsync();
        await Broadcast(gameId);
        return Ok();
    }

    [HttpPost("{gameId}/next-phase")]
    public async Task<IActionResult> NextPhase(Guid gameId)
    {
        try
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var game = await LoadGame(gameId);
            if (game == null) return NotFound();
            if (userId == null || ForbidUnlessActiveGovernment(game, userId) is { }) return Forbid();

            var result = ManeuverEngine.EndPhase(_context, game);
            if (!result.Ok) return BadRequest(result.Error);

            await _context.SaveChangesAsync();
            await Broadcast(gameId);
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NextPhase failed for game {GameId}", gameId);
            return StatusCode(500, ErrorResponses.Internal(HttpContext?.TraceIdentifier));
        }
    }

    [HttpPost("{gameId}/battle-response")]
    public async Task<IActionResult> BattleResponse(Guid gameId, [FromBody] BattleResponseRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var game = await LoadGame(gameId);
        if (game == null) return NotFound();
        if (game.PendingBattleTerritoryId == null || game.PendingBattleAggressorNation == null || !game.PendingBattleDefenders.Any())
        {
            return BadRequest("No pending battle.");
        }

        // The defending nations this caller may answer for: the ones it governs, or ungoverned ones.
        var respondingNations = game.NationStates
            .Where(ns => game.PendingBattleDefenders.Contains(ns.Nation))
            .Where(ns => (ns.ControllerId != null && game.Players.Any(p => p.Id == ns.ControllerId && p.UserId == userId)) || ns.ControllerId == null)
            .Select(ns => ns.Nation)
            .ToList();
        if (!respondingNations.Any()) return Forbid();

        var respondingNation = (request.Nation.HasValue && respondingNations.Contains(request.Nation.Value))
            ? request.Nation.Value
            : respondingNations.First();

        var result = ManeuverEngine.RespondToBattle(_context, game, respondingNation, request.IsFight);
        if (!result.Ok) return BadRequest(result.Error);

        var toast = result.Fought
            ? ToastBuilder.BuildBattleResponseToast(result.RespondingNation, result.AggressorNation, isFight: true)
            : ToastBuilder.BuildBattleResponseToast(result.RespondingNation, result.RespondingNation, isFight: false);
        await _hubContext.Clients.Group(gameId.ToString()).SendAsync("ShowToast", toast, false);

        // The aggressor's maneuver resumes once the battle is closed; flags are settled at its phase end.
        if (result.BattleClosed) ManeuverEngine.TryAutoAdvanceManeuver(_context, game, result.AggressorNation);

        await _context.SaveChangesAsync();
        await Broadcast(gameId);
        if (game.PendingBattleDefenders.Any()) _botService.TriggerBotTurn(gameId);
        return Ok();
    }

    /// <summary>Flag placement for the current board - used by replay to settle flags at a turn's end.</summary>
    public Task UpdateTerritoryControl(Game game)
    {
        ManeuverEngine.UpdateTerritoryControl(_context, game);
        return Task.CompletedTask;
    }
}
