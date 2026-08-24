using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Imperial2030.Server;
using Imperial2030.Server.Configuration;
using Imperial2030.Shared.Auth;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace Imperial2030.Tests
{
    /// <summary>
    /// Rate limiting is deliberately raised out of the way here so these tests exercise account lockout
    /// alone. The 429 path has its own class (<see cref="AuthRateLimitTests"/>) with its own host, so the
    /// two limits cannot interfere with each other's request budgets.
    /// </summary>
    public class UnthrottledAuthFactory : RealAuthWebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("RateLimiting:AuthPermitLimit", "1000");
        }
    }

    /// <summary>
    /// Brute-force protection on sign-in. Login previously passed lockoutOnFailure: false, so Identity's
    /// lockout machinery was registered but never engaged — password guessing was unbounded, and with no
    /// rate limiting anywhere in the pipeline there was nothing else slowing it down either.
    /// </summary>
    public class AuthHardeningTests : IClassFixture<UnthrottledAuthFactory>
    {
        private readonly UnthrottledAuthFactory _factory;

        public AuthHardeningTests(UnthrottledAuthFactory factory) => _factory = factory;

        private const string CorrectPassword = "Correct@12345";
        private const string WrongPassword = "Wrong@12345";

        private static async Task<string> RegisterUserAsync(HttpClient client)
        {
            var userName = $"lock_{Guid.NewGuid():N}".Substring(0, 18);
            var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
            {
                UserName = userName,
                Email = $"{userName}@example.com",
                Password = CorrectPassword,
                ConfirmPassword = CorrectPassword
            });
            response.EnsureSuccessStatusCode();
            return userName;
        }

        private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string userName, string password) =>
            client.PostAsJsonAsync("/api/auth/login", new LoginRequest { UserName = userName, Password = password });

        [Fact]
        public async Task Login_AfterTooManyFailedAttempts_LocksTheAccountEvenWithTheCorrectPassword()
        {
            var client = _factory.CreateClient();
            var userName = await RegisterUserAsync(client);

            for (int attempt = 1; attempt <= AuthSecurity.MaxFailedAccessAttempts; attempt++)
            {
                var failed = await LoginAsync(client, userName, WrongPassword);
                Assert.Equal(HttpStatusCode.BadRequest, failed.StatusCode);
            }

            // The credentials are now correct, but the account must still be refused.
            var response = await LoginAsync(client, userName, CorrectPassword);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var result = await response.Content.ReadFromJsonAsync<LoginResult>();
            Assert.False(result!.Successful);
            Assert.Contains("locked", result.Error!, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// A successful sign-in must clear the failed-attempt counter, or a user who mistypes their
        /// password a few times across a week would eventually lock themselves out for no reason.
        /// </summary>
        [Fact]
        public async Task Login_SuccessfulAttempt_ResetsTheFailureCounter()
        {
            var client = _factory.CreateClient();
            var userName = await RegisterUserAsync(client);

            for (int attempt = 1; attempt < AuthSecurity.MaxFailedAccessAttempts; attempt++)
            {
                await LoginAsync(client, userName, WrongPassword);
            }

            var good = await LoginAsync(client, userName, CorrectPassword);
            Assert.Equal(HttpStatusCode.OK, good.StatusCode);

            // Counter is back to zero, so one more failure must not lock the account.
            await LoginAsync(client, userName, WrongPassword);

            var stillUsable = await LoginAsync(client, userName, CorrectPassword);
            Assert.Equal(HttpStatusCode.OK, stillUsable.StatusCode);
            var result = await stillUsable.Content.ReadFromJsonAsync<LoginResult>();
            Assert.True(result!.Successful);
            Assert.False(string.IsNullOrEmpty(result.Token));
        }

        /// <summary>
        /// An unknown username must stay indistinguishable from a wrong password. Registration already
        /// discloses whether a username is taken, so lockout does not widen that; what must not happen is
        /// the *login* endpoint becoming an enumeration oracle.
        /// </summary>
        [Fact]
        public async Task Login_UnknownUser_ReturnsTheSameErrorAsAWrongPassword()
        {
            var client = _factory.CreateClient();
            var userName = await RegisterUserAsync(client);

            var wrongPassword = await LoginAsync(client, userName, WrongPassword);
            var unknownUser = await LoginAsync(client, $"nobody_{Guid.NewGuid():N}".Substring(0, 18), WrongPassword);

            Assert.Equal(wrongPassword.StatusCode, unknownUser.StatusCode);

            var wrongPasswordBody = await wrongPassword.Content.ReadFromJsonAsync<LoginResult>();
            var unknownUserBody = await unknownUser.Content.ReadFromJsonAsync<LoginResult>();
            Assert.Equal(wrongPasswordBody!.Error, unknownUserBody!.Error);
        }
    }
}
