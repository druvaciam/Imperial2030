namespace Imperial2030.Shared.Models;

public class NationStateDto
{
    public Nation Nation { get; set; }
    public int Treasury { get; set; }
    public int Power { get; set; }
    public int? RondelPosition { get; set; }
    public string? ControllerName { get; set; }
    public bool HasBuiltThisTurn { get; set; }
    public int TaxChartPosition { get; set; }
    public bool HasMovedThisTurn { get; set; }
    public bool HasProducedThisTurn { get; set; }
    public bool HasImportedThisTurn { get; set; }
}
