using System;

namespace Imperial2030.Shared.Models;

public class Unit
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Nation Nation { get; set; }
    public UnitType UnitType { get; set; }
    public string TerritoryId { get; set; } = string.Empty;
    public bool IsHostile { get; set; } = true;

    public Guid GameId { get; set; }
    // Navigation property will be defined in Server/Models/Game.cs context or handled via EF in Server
}
