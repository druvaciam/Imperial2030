namespace Imperial2030.Shared.Models;

public class GameActionDto
{
    public Guid Id { get; set; }
    public long OrderIndex { get; set; }
    public DateTime Timestamp { get; set; }
    public string PlayerName { get; set; } = string.Empty;
    public Nation? Nation { get; set; }
    public string ActionType { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Metadata { get; set; } = string.Empty;
}
