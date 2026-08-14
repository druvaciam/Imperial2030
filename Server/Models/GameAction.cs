using Imperial2030.Shared.Models;
using System.ComponentModel.DataAnnotations;

namespace Imperial2030.Server.Models;

public class GameAction
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid GameId { get; set; }
    public Game Game { get; set; } = default!;

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public long OrderIndex { get; set; }

    [MaxLength(50)]
    public string PlayerName { get; set; } = string.Empty;

    public Nation? Nation { get; set; }

    [MaxLength(50)]
    public string ActionType { get; set; } = string.Empty; // e.g., "Move", "Tax", "Import"

    [MaxLength(500)]
    public string Message { get; set; } = string.Empty;

    public string Metadata { get; set; } = string.Empty;
}
