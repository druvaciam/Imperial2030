namespace Imperial2030.Shared.Models;

public class GameExportDto
{
    public int FormatVersion { get; set; } = 1;
    public Guid OriginalGameId { get; set; }
    public string OriginalGameName { get; set; } = string.Empty;
    public DateTime ExportedAt { get; set; }
    public List<GameActionDto> Actions { get; set; } = new();
}
