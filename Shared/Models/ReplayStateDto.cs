namespace Imperial2030.Shared.Models;

public class ReplayStateDto
{
    public Guid ReplaySessionId { get; set; }
    public Guid SourceGameId { get; set; }
    public int CurrentActionIndex { get; set; }
    public int TotalActions { get; set; }
    public bool IsPaused { get; set; }
    public bool IsComplete { get; set; }
    public string? ErrorMessage { get; set; }
    public GameDetailDto? Game { get; set; }
}
