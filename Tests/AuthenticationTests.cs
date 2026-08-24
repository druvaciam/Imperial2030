using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Imperial2030.Server;
using Imperial2030.Shared.Auth;
using Imperial2030.Shared.Models;
using Xunit;

namespace Imperial2030.Tests
{
    /// <summary>
    /// Covers the real JWT pipeline end to end — token issuance in AuthController and validation in
    /// Program.cs's JwtBearerEvents. Uses <see cref="RealAuthWebApplicationFactory{TStartup}"/> rather
    /// than CustomWebApplicationFactory precisely because the latter replaces authentication with a stub
    /// and therefore cannot see bugs in any of this.
    /// </summary>
    public class AuthenticationTests : IClassFixture<RealAuthWebApplicationFactory<Program>>
    {
        private readonly RealAuthWebApplicationFactory<Program> _factory;

        public AuthenticationTests(RealAuthWebApplicationFactory<Program> factory) => _factory = factory;

        private static CreateGameRequest NewGameRequest() =>
            new() { Name = "Auth Test Game", MaxPlayers = 4, IsPrivate = false };

        private static HttpClient WithToken(HttpClient client, string token)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client;
        }

        private async Task<string> GuestTokenAsync(HttpClient client)
        {
            var response = await client.PostAsync("/api/auth/guest-login", content: null);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<LoginResult>();
            Assert.NotNull(result);
            Assert.True(result!.Successful);
            Assert.False(string.IsNullOrEmpty(result.Token));
            return result.Token!;
        }

        /// <summary>
        /// The regression test for the guest-login outage. GuestLogin mints a token whose NameIdentifier
        /// is a fresh Guid that deliberately has no ApplicationUser row, but OnTokenValidated used to look
        /// up EVERY token's subject in the user store and call context.Fail() when it was missing — so the
        /// server rejected its own freshly-issued guest tokens with 401 on every authorized endpoint.
        ///
        /// 403 (not 401) is the assertion that matters: it proves the token authenticated successfully and
        /// the request actually reached GamesController.CreateGame's `IsInRole("Guest")` check, which had
        /// been unreachable dead code. 401 here means authentication itself failed.
        /// </summary>
        [Fact]
        public async Task GuestToken_IsAccepted_AndReachesTheGuestAuthorizationCheck()
        {
            var client = _factory.CreateClient();
            var token = await GuestTokenAsync(client);

            var response = await WithToken(client, token).PostAsJsonAsync("/api/games", NewGameRequest());

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        /// <summary>A guest may still read the anonymous endpoints the lobby needs.</summary>
        [Fact]
        public async Task GuestToken_CanReadAnonymousEndpoints()
        {
            var client = _factory.CreateClient();
            var token = await GuestTokenAsync(client);

            var response = await WithToken(client, token).GetAsync("/api/games");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        /// <summary>
        /// Guards the other side of the OnTokenValidated fix: a real registered user must still be
        /// validated against the user store, and must NOT be treated as a guest.
        /// </summary>
        [Fact]
        public async Task RegisteredUserToken_IsAccepted_AndMayCreateAGame()
        {
            var client = _factory.CreateClient();
            var userName = $"authuser_{System.Guid.NewGuid():N}".Substring(0, 20);
            const string password = "Test@12345";

            var register = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
            {
                UserName = userName,
                Email = $"{userName}@example.com",
                Password = password,
                ConfirmPassword = password
            });
            Assert.Equal(HttpStatusCode.OK, register.StatusCode);

            var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
            {
                UserName = userName,
                Password = password
            });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            var loginResult = await login.Content.ReadFromJsonAsync<LoginResult>();
            Assert.NotNull(loginResult?.Token);

            var response = await WithToken(client, loginResult!.Token!).PostAsJsonAsync("/api/games", NewGameRequest());

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        /// <summary>
        /// ManeuverController carried a bare [Authorize] while every write in GamesController also refused
        /// the Guest role. Guests could not actually reach a maneuver endpoint (JoinGame refuses them, so
        /// they never become a Player and every handler's `controller.UserId != userId` check fails), but
        /// the asymmetry meant the whole controller relied on that indirect argument holding forever.
        ///
        /// 403 rather than 401 is again the point: the token authenticated, and authorization refused it.
        /// </summary>
        [Fact]
        public async Task GuestToken_IsRefusedByManeuverEndpoints()
        {
            var client = _factory.CreateClient();
            var token = await GuestTokenAsync(client);

            var response = await WithToken(client, token)
                .PostAsync($"/api/maneuver/{System.Guid.NewGuid()}/next-phase", content: null);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        /// <summary>
        /// The other side of the same policy: a real user must still get through to the handler. The game
        /// id is fabricated, so 404 is the expected outcome - what matters is that it is not 403.
        /// </summary>
        [Fact]
        public async Task RegisteredUserToken_IsNotRefusedByManeuverEndpoints()
        {
            var client = _factory.CreateClient();
            var userName = $"manuser_{System.Guid.NewGuid():N}".Substring(0, 20);
            const string password = "Test@12345";

            var register = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
            {
                UserName = userName,
                Email = $"{userName}@example.com",
                Password = password,
                ConfirmPassword = password
            });
            Assert.Equal(HttpStatusCode.OK, register.StatusCode);

            var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
            {
                UserName = userName,
                Password = password
            });
            var loginResult = await login.Content.ReadFromJsonAsync<LoginResult>();
            Assert.NotNull(loginResult?.Token);

            var response = await WithToken(client, loginResult!.Token!)
                .PostAsync($"/api/maneuver/{System.Guid.NewGuid()}/next-phase", content: null);

            Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public async Task NoToken_IsRejectedWithUnauthorized()
        {
            var client = _factory.CreateClient();

            var response = await client.PostAsJsonAsync("/api/games", NewGameRequest());

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        /// <summary>A token signed with the wrong key must not authenticate, guest role or not.</summary>
        [Fact]
        public async Task TokenSignedWithWrongKey_IsRejected()
        {
            var client = _factory.CreateClient();
            var forged = ForgeGuestToken("A_Completely_Different_Signing_Key_0123456789!");

            var response = await WithToken(client, forged).PostAsJsonAsync("/api/games", NewGameRequest());

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        private static string ForgeGuestToken(string signingKey)
        {
            var claims = new[]
            {
                new System.Security.Claims.Claim(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub, System.Guid.NewGuid().ToString()),
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, System.Guid.NewGuid().ToString()),
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "Guest")
            };
            var key = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(signingKey));
            var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
                issuer: "Imperial2030Server",
                audience: "Imperial2030Client",
                claims: claims,
                expires: System.DateTime.UtcNow.AddDays(1),
                signingCredentials: new Microsoft.IdentityModel.Tokens.SigningCredentials(key, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256));
            return new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);
        }
    }
}
