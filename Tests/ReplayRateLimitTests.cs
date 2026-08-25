using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Imperial2030.Server;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace Imperial2030.Tests
{
    public class ThrottledReplayFactory : RealAuthWebApplicationFactory<Program>
    {
        public const int PermitLimit = 3;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("RateLimiting:ReplayPermitLimit", PermitLimit.ToString());
            builder.UseSetting("RateLimiting:ReplayWindowSeconds", "60");
        }
    }

    /// <summary>
    /// The rate half of the replay protection. ReplaySessionManager's caps bound how many sessions a caller
    /// can HOLD at once; they say nothing about churn — start, stop, start again never exceeds the cap yet
    /// costs a full source-game load and reseed every cycle. This policy bounds the rate as well.
    /// </summary>
    public class ReplayRateLimitTests : IClassFixture<ThrottledReplayFactory>
    {
        private readonly ThrottledReplayFactory _factory;

        public ReplayRateLimitTests(ThrottledReplayFactory factory) => _factory = factory;

        /// <summary>
        /// Uses a game id that does not exist on purpose. Inside the budget the request reaches the
        /// endpoint and 404s; past it the limiter refuses before the endpoint runs at all. The status
        /// flipping from 404 to 429 is exactly the boundary being asserted.
        /// </summary>
        [Fact]
        public async Task StartReplay_BeyondThePermitLimit_IsRejectedWithTooManyRequests()
        {
            var client = _factory.CreateClient();
            var missingGameId = Guid.NewGuid();

            for (int i = 1; i <= ThrottledReplayFactory.PermitLimit; i++)
            {
                var allowed = await client.PostAsync($"/api/games/{missingGameId}/replay/start", content: null);
                Assert.True(allowed.StatusCode != HttpStatusCode.TooManyRequests,
                    $"Request {i} of {ThrottledReplayFactory.PermitLimit} was throttled inside its own budget.");
            }

            var blocked = await client.PostAsync($"/api/games/{missingGameId}/replay/start", content: null);

            Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
            Assert.True(blocked.Headers.Contains("Retry-After"),
                "A throttled caller should be told when to come back.");
        }

        /// <summary>
        /// The replay policy must not consume the auth budget or vice versa — they are separate partitions
        /// with different costs, and sharing one would let replay traffic lock users out of signing in.
        /// </summary>
        [Fact]
        public async Task ReplayThrottling_DoesNotAffectOtherEndpoints()
        {
            var client = _factory.CreateClient();
            var missingGameId = Guid.NewGuid();

            for (int i = 0; i <= ThrottledReplayFactory.PermitLimit * 2; i++)
            {
                await client.PostAsync($"/api/games/{missingGameId}/replay/start", content: null);
            }

            var games = await client.GetAsync("/api/games");
            Assert.Equal(HttpStatusCode.OK, games.StatusCode);

            var guest = await client.PostAsync("/api/auth/guest-login", content: null);
            Assert.Equal(HttpStatusCode.OK, guest.StatusCode);
        }
    }
}
