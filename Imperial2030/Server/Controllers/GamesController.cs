using Imperial2030.Server.Data;
using Imperial2030.Server.Models;
using Imperial2030.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;

namespace Imperial2030.Server.Controllers;

[Route("api/[controller]")]
[ApiController]
[Authorize]
public class GamesController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IHubContext<Imperial2030.Server.Hubs.GameHub> _hubContext;

    public GamesController(ApplicationDbContext context, UserManager<ApplicationUser> userManager, IHubContext<Imperial2030.Server.Hubs.GameHub> hubContext)
    {
        _context = context;
        _userManager = userManager;
        _hubContext = hubContext;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<GameDto>>> GetGames()
    {
        return await _context.Games
            .Include(g => g.Players)
            .OrderByDescending(g => g.CreatedAt)
            .Select(g => new GameDto
            {
                Id = g.Id,
                Name = g.Name,
                Status = g.Status,
                CreatedAt = g.CreatedAt,
                PlayerCount = g.Players.Count
            })
            .ToListAsync();
    }

    [HttpPost]
    public async Task<ActionResult<GameDto>> CreateGame([FromBody] string gameName)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var game = new Game { Name = gameName };
        _context.Games.Add(game);

        var player = new Player
        {
            GameId = game.Id,
            UserId = userId,
            IsHost = true
        };
        _context.Players.Add(player);

        await _context.SaveChangesAsync();

        var gameDto = new GameDto
        {
            Id = game.Id,
            Name = game.Name,
            Status = game.Status,
            CreatedAt = game.CreatedAt,
            PlayerCount = 1
        };

        await _hubContext.Clients.All.SendAsync("GameCreated", gameDto);

        return CreatedAtAction(nameof(GetGames), new { id = game.Id }, gameDto);
    }

    [HttpPost("{gameId}/join")]
    public async Task<IActionResult> JoinGame(Guid gameId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var game = await _context.Games.Include(g => g.Players).FirstOrDefaultAsync(g => g.Id == gameId);
        if (game == null) return NotFound();

        if (game.Players.Any(p => p.UserId == userId))
            return BadRequest("You are already in this game.");

        if (game.Players.Count >= 6)
            return BadRequest("Game is full.");

        var player = new Player
        {
            GameId = game.Id,
            UserId = userId,
            IsHost = false
        };
        _context.Players.Add(player);
        await _context.SaveChangesAsync();

        await _hubContext.Clients.All.SendAsync("GameUpdated", gameId);

        return Ok();
    }
    [HttpPost("{gameId}/leave")]
    public async Task<IActionResult> LeaveGame(Guid gameId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var game = await _context.Games.Include(g => g.Players).FirstOrDefaultAsync(g => g.Id == gameId);
        if (game == null) return NotFound();

        var player = game.Players.FirstOrDefault(p => p.UserId == userId);
        if (player == null) return BadRequest("You are not in this game.");

        _context.Players.Remove(player);

        // If the player was the host, assign a new host if there are other players
        if (player.IsHost)
        {
            var newHost = game.Players.FirstOrDefault(p => p.UserId != userId);
            if (newHost != null)
            {
                newHost.IsHost = true;
            }
        }

        await _context.SaveChangesAsync();

        // If no players left, delete the game
        // Reload game to check players count correctly after save (or just check the tracked collection if it was updated)
        // EF Core tracks the collection, but let's be safe and check the database count or the in-memory collection which should be updated.
        // Actually, _context.Players.Remove(player) removes it from the collection on the context.
        if (!game.Players.Any())
        {
            _context.Games.Remove(game);
            await _context.SaveChangesAsync();
        }
        
        await _hubContext.Clients.All.SendAsync("GameUpdated", gameId);

        return Ok();
    }

    [HttpGet("{gameId}")]
    public async Task<ActionResult<GameDetailDto>> GetGame(Guid gameId)
    {
        var game = await _context.Games
            .Include(g => g.Players)
            .ThenInclude(p => p.User)
            .FirstOrDefaultAsync(g => g.Id == gameId);

        if (game == null) return NotFound();

        return new GameDetailDto
        {
            Id = game.Id,
            Name = game.Name,
            Status = game.Status,
            CreatedAt = game.CreatedAt,
            PlayerCount = game.Players.Count,
            Players = game.Players.Select(p => new PlayerDto
            {
                Id = p.Id,
                UserId = p.UserId,
                UserName = p.User?.UserName ?? "Unknown",
                IsHost = p.IsHost
            }).ToList()
        };
    }

    [HttpPost("{gameId}/start")]
    public async Task<IActionResult> StartGame(Guid gameId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var game = await _context.Games.Include(g => g.Players).FirstOrDefaultAsync(g => g.Id == gameId);
        if (game == null) return NotFound();

        var player = game.Players.FirstOrDefault(p => p.UserId == userId);
        if (player == null) return BadRequest("You are not in this game.");

        if (!player.IsHost) return Forbid();

        if (game.Players.Count < 2) return BadRequest("Need at least 2 players to start.");

        if (game.Status != GameStatus.Lobby) return BadRequest("Game is not in lobby state.");

        game.Status = GameStatus.InProgress;
        await _context.SaveChangesAsync();
        
        await _hubContext.Clients.All.SendAsync("GameUpdated", gameId);
        await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameStarted", gameId);

        return Ok();
    }
}
