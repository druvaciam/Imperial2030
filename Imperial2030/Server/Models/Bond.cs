using Imperial2030.Shared.Models;
using System.ComponentModel.DataAnnotations.Schema;

namespace Imperial2030.Server.Models;

public class Bond
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Nation Nation { get; set; }
    public int Cost { get; set; }
    public int Interest { get; set; }

    public Guid? HolderId { get; set; }
    [ForeignKey(nameof(HolderId))]
    public virtual Player? Holder { get; set; }

    public Guid GameId { get; set; }
    [ForeignKey(nameof(GameId))]
    public virtual Game? Game { get; set; }
}
