using Imperial2030.Shared.Models;
using System.ComponentModel.DataAnnotations;

namespace Imperial2030.Server.Models;

public class Game
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(50)]
    public string Name { get; set; } = string.Empty;

    public GameStatus Status { get; set; } = GameStatus.Lobby;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public virtual ICollection<Player> Players { get; set; } = new List<Player>();
}
