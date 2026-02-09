using System;
using System.ComponentModel.DataAnnotations;
using Imperial2030.Shared.Models;
using System.ComponentModel.DataAnnotations.Schema;

namespace Imperial2030.Server.Models;

public class TerritoryState
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public string TerritoryId { get; set; } = string.Empty; // Maps to implicit static definition

    public bool HasFactory { get; set; } = false;
    
    // The Nation that controls this territory (Flag)
    public Nation? Controller { get; set; }

    // Potential for future: public int ArmyCount { get; set; }

    public Guid GameId { get; set; }
    [ForeignKey(nameof(GameId))]
    public virtual Game? Game { get; set; }
}
