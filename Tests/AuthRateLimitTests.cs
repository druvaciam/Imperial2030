using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Imperial2030.Server;
using Imperial2030.Shared.Auth;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace Imperial2030.Tests
{
    /// <summary>
    /// Drives the auth rate limit down to a handful of requests so the 429 path can be proven in a few
    /// calls instead of firing the real production budget. Its own host, so exhausting this limiter
    /// cannot starve any other test class.
    /// </summary>
    public class ThrottledAuthFactory : RealAuthWebApplicationFactory<Program>
    {
        public const int PermitLimit = 3;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("RateLimiting:AuthPermitLimit", PermitLimit.ToString());
            builder.UseSetting("RateLimiting:AuthWindowSeconds", "60");
        }
    }

    /// <summary>
    /// Account lockout stops guessing at one known account; it does nothing against an attacker sweeping
    /// many usernames, and nothing against unbounded guest-token minting. The rate limiter covers both by
    /// capping every /api/auth/* endpoint per caller.
    /// </summary>
    public class AuthRateLimitTests : IClassFixture<ThrottledAuthFactory>
    {
        private readonly ThrottledAuthFactory _factory;

        public AuthRateLimitTests(ThrottledAuthFactory factory) => _factory = factory;

        /// <summary>
        /// Deliberately one test rather than two. The permit budget is per host and the window is a full
        /// minute, so a second test sharing this class fixture would start from an already-exhausted
        /// limiter and see a 429 on its first request — which looks exactly like the limiter being broken.
        /// Asserting the allowed run and the rejection together keeps the budget accounted for.
        /// </summary>
        [Fact]
        public async Task Login_BeyondThePermitLimit_IsRejectedWithTooManyRequestsAndRetryAfter()
        {
            var client = _factory.CreateClient();

            // Everything inside the budget must get through to the (failing) sign-in, not be throttled.
            for (int i = 1; i <= ThrottledAuthFactory.PermitLimit; i++)
            {
                var allowed = await Attempt(client);
                Assert.True(allowed.StatusCode != HttpStatusCode.TooManyRequests,
                    $"Request {i} of {ThrottledAuthFactory.PermitLimit} was throttled; the limiter is rejecting inside its own budget.");
            }

            var blocked = await Attempt(client);

            Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
            Assert.True(blocked.Headers.Contains("Retry-After"),
                "A throttled caller should be told when to come back, not just refused.");
        }

        /// <summary>
        /// The limiter must sit on the auth endpoints only. Throttling gameplay would make a busy game
        /// unplayable, which is a worse outcome than the brute-force risk it would be mitigating.
        /// </summary>
        [Fact]
        public async Task GameEndpoints_AreNotRateLimitedByTheAuthPolicy()
        {
            var client = _factory.CreateClient();

            for (int i = 0; i <= ThrottledAuthFactory.PermitLimit * 3; i++)
            {
                var response = await client.GetAsync("/api/games");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
        }

        private static Task<HttpResponseMessage> Attempt(HttpClient client) =>
            client.PostAsJsonAsync("/api/auth/login", new LoginRequest
            {
                UserName = "someone",
                Password = "Whatever@12345"
            });
    }
}
