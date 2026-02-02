using System.ComponentModel.DataAnnotations;

namespace Imperial2030.Shared.Auth;

public class LoginRequest
{
    [Required]
    public string UserName { get; set; } = string.Empty;
    [Required]
    public string Password { get; set; } = string.Empty;
}

public class RegisterRequest
{
    [Required]
    public string UserName { get; set; } = string.Empty;
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;
    [Required]
    public string Password { get; set; } = string.Empty;
    [Required]
    [Compare(nameof(Password), ErrorMessage = "Passwords do not match")]
    public string ConfirmPassword { get; set; } = string.Empty;
}

public class LoginResult
{
    public bool Successful { get; set; }
    public string? Error { get; set; }
    public string? Token { get; set; }
}
