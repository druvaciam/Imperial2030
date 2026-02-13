namespace Imperial2030.Shared.Models;

public class TerritoryStateDto
{
    public string TerritoryId { get; set; } = string.Empty;
    public bool HasFactory { get; set; }
    public Nation? Controller { get; set; }
    // Future: public List<UnitDto> Units { get; set; }
}
