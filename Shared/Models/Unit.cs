using System;

namespace Imperial2030.Shared.Models;

public class Unit
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Nation Nation { get; set; }
    public UnitType UnitType { get; set; }
    public string TerritoryId { get; set; } = string.Empty;
    public bool IsHostile { get; set; } = true;
    public bool HasMoved { get; set; }
    public bool HasConvoyed { get; set; } // Tracks if this fleet has transported an army this turn

    public Guid GameId { get; set; }
    // Navigation property will be defined in Server/Models/Game.cs context or handled via EF in Server
}
