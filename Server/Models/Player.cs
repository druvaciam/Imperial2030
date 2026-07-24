using Imperial2030.Shared.Models;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Imperial2030.Server.Models;

public class Player
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string? UserId { get; set; }
    [ForeignKey(nameof(UserId))]
    public virtual ApplicationUser? User { get; set; }

    public Guid GameId { get; set; }
    [ForeignKey(nameof(GameId))]
    public virtual Game? Game { get; set; }

    public bool IsHost { get; set; }
    public bool IsBot { get; set; } = false;
    public string? BotName { get; set; }
    public string? BotType { get; set; }

    // Gameplay specific properties
    public int Cash { get; set; } = 0; // Starting cash depends on starting nation or generic start

    public virtual ICollection<Bond> Bonds { get; set; } = new List<Bond>();
}
