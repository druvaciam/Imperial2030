namespace Imperial2030.Shared.Models;

public class BondDto
{
    public Guid Id { get; set; }
    public Nation Nation { get; set; }
    public int Cost { get; set; }
    public int Interest { get; set; }
    // Null value for HolderName implies the bond is in the bank (unsold)
    public string? HolderName { get; set; }
}
