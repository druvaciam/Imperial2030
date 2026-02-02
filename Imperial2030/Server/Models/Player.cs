using Imperial2030.Shared.Models;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Imperial2030.Server.Models;

public class Player
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string UserId { get; set; } = string.Empty;
    [ForeignKey(nameof(UserId))]
    public virtual ApplicationUser? User { get; set; }

    public Guid GameId { get; set; }
    [ForeignKey(nameof(GameId))]
    public virtual Game? Game { get; set; }

    public bool IsHost { get; set; }

    // Gameplay specific properties
    public int Cash { get; set; } = 0; // Starting cash depends on starting nation or generic start

    // In Imperial, players don't "play as" a nation fixedly, but we might track which nation they currently control or sit at if using a variant.
    // For now, these are the basics.
}
