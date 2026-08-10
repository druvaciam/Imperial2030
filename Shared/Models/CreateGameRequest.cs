using System.ComponentModel.DataAnnotations;

namespace Imperial2030.Shared.Models;

public class CreateGameRequest
{
    [Required]
    [StringLength(50, ErrorMessage = "Game name cannot exceed 50 characters.")]
    public string Name { get; set; } = string.Empty;

    [Range(2, 6)]
    public int MaxPlayers { get; set; } = 6;

    public bool IsPrivate { get; set; } = false;
    public bool VariantBonusOnlyForTaxIncreases { get; set; } = false;
}
