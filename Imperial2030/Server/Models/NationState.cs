using Imperial2030.Shared.Models;
using System.ComponentModel.DataAnnotations.Schema;

namespace Imperial2030.Server.Models;

public class NationState
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Nation Nation { get; set; }
    public int Treasury { get; set; } = 0;
    public int Power { get; set; } = 0;
    public int? RondelPosition { get; set; } = 0;

    public Guid? ControllerId { get; set; }
    [ForeignKey(nameof(ControllerId))]
    public virtual Player? Controller { get; set; }

    public Guid GameId { get; set; }
    [ForeignKey(nameof(GameId))]
    public virtual Game? Game { get; set; }
}
