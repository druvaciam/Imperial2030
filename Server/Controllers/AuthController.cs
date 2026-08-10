using Imperial2030.Server.Models;
using Imperial2030.Shared.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace Imperial2030.Server.Controllers;

[Route("api/[controller]")]
[ApiController]
public class AuthController : ControllerBase
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly string _jwtKey;

    public AuthController(UserManager<ApplicationUser> userManager, SignInManager<ApplicationUser> signInManager, IConfiguration configuration)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _jwtKey = configuration["Jwt:Key"] ?? "ThisIsASecretKeyForImperial2030GameOnly!";
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
        var result = await _signInManager.PasswordSignInAsync(request.UserName, request.Password, false, false);

        if (result.Succeeded)
        {
            var user = await _userManager.FindByNameAsync(request.UserName);
            if (user == null) return Unauthorized();

            var token = GenerateJwtToken(user);
            return Ok(new LoginResult { Successful = true, Token = token });
        }

        return BadRequest(new LoginResult { Successful = false, Error = "Invalid login attempt." });
    }

    [HttpPost("guest-login")]
    public IActionResult GuestLogin()
    {
        var guestId = Guid.NewGuid().ToString();
        var guestName = "Guest_" + guestId.Substring(0, 6);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, guestId),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.NameIdentifier, guestId),
            new Claim(ClaimTypes.Name, guestName),
            new Claim(ClaimTypes.Role, "Guest")
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: "Imperial2030Server",
            audience: "Imperial2030Client",
            claims: claims,
            expires: DateTime.Now.AddDays(1),
            signingCredentials: creds
        );

        var tokenString = new JwtSecurityTokenHandler().WriteToken(token);
        return Ok(new LoginResult { Successful = true, Token = tokenString });
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

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: "Imperial2030Server",
            audience: "Imperial2030Client",
            claims: claims,
            expires: DateTime.Now.AddDays(1),
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
