using Imperial2030.Server.Configuration;
using Imperial2030.Server.Models;
using Imperial2030.Shared.Auth;
using Imperial2030.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace Imperial2030.Server.Controllers;

[Route("api/[controller]")]
[ApiController]
// Covers login (password guessing), register (account-creation spam) and guest-login (unbounded token
// minting) in one place. Account lockout alone only protects a single known account; this caps the whole
// auth surface per caller. See RateLimitPolicies.
[EnableRateLimiting(RateLimitPolicies.Auth)]
public class AuthController : ControllerBase
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly JwtOptions _jwtOptions;

    // Takes the JwtOptions singleton resolved and validated at startup rather than re-reading
    // configuration here. This class previously carried its own copy of the signing-key fallback, plus
    // its own issuer/audience/expiry literals — so a key configured for validation in Program.cs but not
    // here (or vice versa) produced tokens that the server itself would reject.
    public AuthController(UserManager<ApplicationUser> userManager, SignInManager<ApplicationUser> signInManager, JwtOptions jwtOptions)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _jwtOptions = jwtOptions;
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        var user = new ApplicationUser { UserName = request.UserName, Email = request.Email };
        var result = await _userManager.CreateAsync(user, request.Password);

        if (result.Succeeded)
        {
            return Ok(new LoginResult { Successful = true });
        }

        return BadRequest(new LoginResult { Successful = false, Error = string.Join(", ", result.Errors.Select(e => e.Description)) });
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        // lockoutOnFailure: true is the whole point — it was previously false, which registered Identity's
        // lockout machinery without ever engaging it, leaving password guessing unbounded. A successful
        // sign-in resets the counter automatically, so ordinary mistyping never accumulates towards a lock.
        var result = await _signInManager.PasswordSignInAsync(
            request.UserName, request.Password, isPersistent: false, lockoutOnFailure: true);

        if (result.Succeeded)
        {
            var user = await _userManager.FindByNameAsync(request.UserName);
            if (user == null) return Unauthorized();

            var token = GenerateJwtToken(user);
            return Ok(new LoginResult { Successful = true, Token = token });
        }

        if (result.IsLockedOut)
        {
            // Naming the lockout is a deliberate trade-off. It confirms the account exists, but
            // registration already rejects a taken username by name, so this discloses nothing new — and
            // a locked-out user who is only told "invalid login" will keep retrying and keep the lock
            // alive. Every other failure (wrong password, unknown user) stays on the single generic
            // message below so login itself never becomes an enumeration oracle.
            return BadRequest(new LoginResult
            {
                Successful = false,
                Error = $"This account is temporarily locked after {AuthSecurity.MaxFailedAccessAttempts} " +
                        $"failed sign-in attempts. Try again in {AuthSecurity.LockoutDuration.TotalMinutes:0} minutes."
            });
        }

        return BadRequest(new LoginResult { Successful = false, Error = "Invalid login attempt." });
    }

    /// <summary>
    /// Confirms the caller's token is still ACCEPTED, not merely unexpired.
    ///
    /// The client can only check `exp` itself — it cannot verify a signature — so after the signing key
    /// is rotated a stale token leaves a user permanently half-logged-in: the header greets them by name
    /// while the server treats them as anonymous. Normal browsing never reveals it, because the lobby
    /// list is [AllowAnonymous] and answers 200 with the caller silently unauthenticated, which shows up
    /// only indirectly as their own games offering "Watch" instead of "Resume" and "My Games Only"
    /// coming back empty. This endpoint is deliberately [Authorize] so that a token the server no longer
    /// honours produces a 401, which CustomAuthorizationMessageHandler turns into a clean sign-out.
    /// </summary>
    [HttpGet("me")]
    [Authorize]
    public IActionResult Me()
    {
        return Ok(new CurrentUserDto
        {
            UserId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty,
            UserName = User.Identity?.Name ?? string.Empty,
            IsGuest = User.IsInRole(GameConstants.GuestRole)
        });
    }

    [HttpPost("guest-login")]
    public IActionResult GuestLogin()
    {
        var guestId = Guid.NewGuid().ToString();
        var guestName = "Guest_" + guestId.Substring(0, 6);

        // The Guest role is what lets Program.cs's OnTokenValidated skip its user-store existence check
        // for this token: a guest is a throwaway identity with no ApplicationUser row to find. It is also
        // what GamesController's write endpoints refuse. Same constant on both sides, deliberately.
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, guestId),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.NameIdentifier, guestId),
            new Claim(ClaimTypes.Name, guestName),
            new Claim(ClaimTypes.Role, GameConstants.GuestRole)
        };

        return Ok(new LoginResult { Successful = true, Token = WriteToken(claims) });
    }

    private string GenerateJwtToken(ApplicationUser user)
    {
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.NameIdentifier, user.Id),
            new Claim(ClaimTypes.Name, user.UserName ?? "")
        };

        return WriteToken(claims);
    }

    // Single place where a token is actually signed. Guest and registered tokens differ only in their
    // claims; every other parameter comes from the startup-validated JwtOptions singleton, so neither
    // path can drift from what Program.cs validates against. UtcNow (not Now) because the expiry is
    // serialized as a Unix timestamp — .NET converts a Local kind correctly, but stating UTC removes the
    // question entirely.
    private string WriteToken(IEnumerable<Claim> claims)
    {
        var credentials = new SigningCredentials(_jwtOptions.SigningKey, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: JwtOptions.Issuer,
            audience: JwtOptions.Audience,
            claims: claims,
            expires: DateTime.UtcNow.Add(JwtOptions.TokenLifetime),
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
