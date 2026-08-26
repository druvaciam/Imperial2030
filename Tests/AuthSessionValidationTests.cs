using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Threading.Tasks;
using Imperial2030.Server;
using Imperial2030.Shared.Auth;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Imperial2030.Tests
{
    /// <summary>
    /// Covers /api/auth/me, the endpoint that lets the client find out whether the token it is holding is
    /// still ACCEPTED rather than merely unexpired.
    ///
    /// The gap it closes: CustomAuthenticationStateProvider validates only the token's `exp` claim, never
    /// the signature, so after the signing key is rotated a stale token leaves the user permanently
    /// half-logged-in. The header greets them by name and the lobby loads normally — because
    /// GET /api/games is [AllowAnonymous] and therefore returns 200 with the caller silently treated as
    /// anonymous — while IsCurrentUserInGame comes back false for every game, so their own in-progress
    /// games show "Watch" instead of "Resume" and "My Games Only" is empty. No request 401s, so nothing
    /// tells the client to re-authenticate.
    /// </summary>
    public class AuthSessionValidationTests : IClassFixture<RealAuthWebApplicationFactory<Program>>
    {
        private readonly RealAuthWebApplicationFactory<Program> _factory;

        public AuthSessionValidationTests(RealAuthWebApplicationFactory<Program> factory) => _factory = factory;

        private static HttpClient WithToken(HttpClient client, string token)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client;
        }

        private async Task<string> RegisterAndLoginAsync(HttpClient client, string userName)
        {
            const string password = "Correct@12345";
            var register = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
            {
                UserName = userName,
                Email = $"{userName}@example.com",
                Password = password,
                ConfirmPassword = password
            });
            register.EnsureSuccessStatusCode();

            var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
            {
                UserName = userName,
                Password = password
            });
            login.EnsureSuccessStatusCode();
            var result = await login.Content.ReadFromJsonAsync<LoginResult>();
            return result!.Token!;
        }

        [Fact]
        public async Task Me_WithAValidToken_ReturnsTheSignedInUser()
        {
            var client = _factory.CreateClient();
            var userName = $"me_{Guid.NewGuid():N}".Substring(0, 16);
            var token = await RegisterAndLoginAsync(client, userName);

            var response = await WithToken(client, token).GetAsync("/api/auth/me");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains(userName, body, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The case that matters. A token whose signature no longer verifies must 401 here, so the client
        /// can clear it — unlike the [AllowAnonymous] lobby endpoint, which returns 200 and hides the
        /// problem.
        /// </summary>
        [Fact]
        public async Task Me_WithATokenSignedByARotatedAwayKey_IsRejected()
        {
            var client = _factory.CreateClient();
            var stale = ForgeToken("A_Previous_Signing_Key_That_Has_Been_Rotated_Away_012345");

            var response = await WithToken(client, stale).GetAsync("/api/auth/me");

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task Me_WithNoToken_IsRejected()
        {
            var response = await _factory.CreateClient().GetAsync("/api/auth/me");

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        /// <summary>
        /// Guests are legitimately authenticated and must not be logged out by this check — they have no
        /// ApplicationUser row, which is exactly the case OnTokenValidated has to skip.
        /// </summary>
        [Fact]
        public async Task Me_WithAGuestToken_IsAccepted()
        {
            var client = _factory.CreateClient();
            var guest = await client.PostAsync("/api/auth/guest-login", content: null);
            guest.EnsureSuccessStatusCode();
            var token = (await guest.Content.ReadFromJsonAsync<LoginResult>())!.Token!;

            var response = await WithToken(client, token).GetAsync("/api/auth/me");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        /// <summary>
        /// Documents the asymmetry this endpoint exists to compensate for: the same stale token is
        /// rejected by /api/auth/me but silently accepted-as-anonymous by the lobby list, which is why
        /// the client cannot detect the problem from normal browsing.
        /// </summary>
        [Fact]
        public async Task TheLobbyEndpointStillReturnsOkForAStaleToken_WhichIsWhyMeIsNeeded()
        {
            var client = _factory.CreateClient();
            var stale = ForgeToken("A_Previous_Signing_Key_That_Has_Been_Rotated_Away_012345");

            var lobby = await WithToken(client, stale).GetAsync("/api/games");

            Assert.Equal(HttpStatusCode.OK, lobby.StatusCode);
        }

        private static string ForgeToken(string signingKey)
        {
            var claims = new[]
            {
                new System.Security.Claims.Claim(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()),
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "player1")
            };
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey));
            var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
                issuer: Imperial2030.Server.Configuration.JwtOptions.Issuer,
                audience: Imperial2030.Server.Configuration.JwtOptions.Audience,
                claims: claims,
                expires: DateTime.UtcNow.AddHours(1),
                signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
            return new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);
        }
    }
}
