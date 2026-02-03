namespace Imperial2030.Shared.Models;

public enum CityType
{
    None = 0,
    Brown = 1,    // Arms Industry (Tank)
    LightBlue = 2 // Shipyard (Ship)
}

public class Territory
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public Nation? Nation { get; set; } // Owner (Home Province)
    public CityType CityType { get; set; } = CityType.None;
    
    // Helper to see if it is a home city for a specific nation
    public bool IsHomeCity(Nation nation) => Nation == nation && CityType != CityType.None;
}
