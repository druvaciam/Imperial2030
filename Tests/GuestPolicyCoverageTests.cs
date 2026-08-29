using System;
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
    /// Pins guest refusal across every write endpoint on GamesController.
    ///
    /// These endpoints originally each carried their own inline
    /// `if (User.IsInRole(GuestRole)) return Forbid();`, while ManeuverController used the declarative
    /// `NotGuestPolicy`. Two mechanisms enforcing one rule is how they drift — and an inline check is
    /// something a new endpoint simply forgets. This suite exists so the move onto the shared policy is
    /// provably behaviour-preserving, and so a future endpoint that forgets the policy fails here rather
    /// than silently admitting guests.
    ///
    /// 403 rather than 401 throughout: the guest token authenticates fine, and it is *authorization*
    /// that refuses it. A 401 would mean the guest-login regression had returned.
    /// </summary>
    public class GuestPolicyCoverageTests : IClassFixture<UnthrottledAuthFactory>
    {
        private readonly UnthrottledAuthFactory _factory;

        // Unthrottled: every case here needs its own guest-login or register+login, which would otherwise
        // exhaust the 20-per-minute auth budget partway through the theory and fail later cases with 429
        // rather than the status under test.
        public GuestPolicyCoverageTests(UnthrottledAuthFactory factory) => _factory = factory;

        private async Task<HttpClient> GuestClientAsync()
        {
            var client = _factory.CreateClient();
            var response = await client.PostAsync("/api/auth/guest-login", content: null);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<LoginResult>();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", result!.Token!);
            return client;
        }

        public static TheoryData<string, string> GuestForbiddenEndpoints() => new()
        {
            { "POST",   "/api/games" },
            { "POST",   "/api/games/{id}/join" },
            { "POST",   "/api/games/{id}/leave" },
            { "DELETE", "/api/games/{id}" },
            { "POST",   "/api/games/import" },
            { "POST",   "/api/games/{id}/add-bot" },
            { "POST",   "/api/games/{id}/remove-bot/{playerId}" },
            { "POST",   "/api/games/{id}/start" },
        };

        [Theory]
        [MemberData(nameof(GuestForbiddenEndpoints))]
        public async Task EveryGamesControllerWriteEndpointRefusesGuests(string method, string route)
        {
            var client = await GuestClientAsync();
            var url = route
                .Replace("{id}", Guid.NewGuid().ToString())
                .Replace("{playerId}", Guid.NewGuid().ToString());

            var request = new HttpRequestMessage(new HttpMethod(method), url);
            if (method != "DELETE")
            {
                // A body the endpoint would accept, so a 400 cannot masquerade as the refusal.
                request.Content = JsonContent.Create(new { name = "x", maxPlayers = 4, isPrivate = false });
            }

            var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        /// <summary>
        /// The other half: the same routes must NOT refuse a registered user. Game ids are fabricated, so
        /// 404/400 are fine — what matters is that authorization let the request through to the handler.
        /// Without this, "refuse everyone" would pass the theory above.
        /// </summary>
        [Theory]
        [MemberData(nameof(GuestForbiddenEndpoints))]
        public async Task TheSameEndpointsAdmitARegisteredUser(string method, string route)
        {
            var client = _factory.CreateClient();
            var userName = $"gp_{Guid.NewGuid():N}".Substring(0, 16);
            const string password = "Correct@12345";

            (await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
            {
                UserName = userName,
                Email = $"{userName}@example.com",
                Password = password,
                ConfirmPassword = password
            })).EnsureSuccessStatusCode();

            var login = await client.PostAsJsonAsync("/api/auth/login",
                new LoginRequest { UserName = userName, Password = password });
            var token = (await login.Content.ReadFromJsonAsync<LoginResult>())!.Token!;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var url = route
                .Replace("{id}", Guid.NewGuid().ToString())
                .Replace("{playerId}", Guid.NewGuid().ToString());

            var request = new HttpRequestMessage(new HttpMethod(method), url);
            if (method != "DELETE")
            {
                request.Content = JsonContent.Create(new { name = "x", maxPlayers = 4, isPrivate = false });
            }

            var response = await client.SendAsync(request);

            Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        /// <summary>Guests must keep the read access the watch-only flow depends on.</summary>
        [Fact]
        public async Task GuestsCanStillReadTheLobbyAndWatchGames()
        {
            var client = await GuestClientAsync();

            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/games")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/games/available-bots")).StatusCode);
        }
    }
}
