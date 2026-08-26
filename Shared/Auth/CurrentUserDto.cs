namespace Imperial2030.Shared.Auth;

/// <summary>
/// Response of GET /api/auth/me. Its contents matter less than its status code: a 200 tells the client
/// the token it holds is still accepted by the server, a 401 that it must sign in again.
/// </summary>
public class CurrentUserDto
{
    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public bool IsGuest { get; set; }
}
