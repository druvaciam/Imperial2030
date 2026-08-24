using System;
using System.Collections.Generic;
using Imperial2030.Server.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

namespace Imperial2030.Tests
{
    /// <summary>
    /// Startup validation of the JWT signing key.
    ///
    /// The key previously had a hardcoded fallback committed to the repository, duplicated in both
    /// Program.cs and AuthController. Jwt:Key is set in no appsettings file and the Azure deploy workflow
    /// configures no app settings, so any environment that had not set it manually was silently signing
    /// and accepting tokens with a key that is public in git history — anyone could forge a token for any
    /// user. The failure was invisible: nothing distinguished "configured" from "using the committed
    /// default". These tests pin the fail-fast behaviour that replaced it.
    /// </summary>
    public class JwtOptionsTests
    {
        private static IConfiguration Config(string? jwtKey)
        {
            var values = new Dictionary<string, string?>();
            if (jwtKey != null) values["Jwt:Key"] = jwtKey;
            return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        }

        private static IHostEnvironment Env(string environmentName)
        {
            var mock = new Mock<IHostEnvironment>();
            mock.SetupGet(e => e.EnvironmentName).Returns(environmentName);
            return mock.Object;
        }

        private static readonly IHostEnvironment Production = Env(Environments.Production);
        private static readonly IHostEnvironment Development = Env(Environments.Development);

        private const string ValidKey = "A_Perfectly_Fine_Production_Signing_Key_0123456789";

        [Fact]
        public void Resolve_OutsideDevelopment_ThrowsWhenKeyIsMissing()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => JwtOptions.Resolve(Config(null), Production));
            Assert.Contains("Jwt:Key", ex.Message);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void Resolve_OutsideDevelopment_ThrowsWhenKeyIsBlank(string key)
        {
            Assert.Throws<InvalidOperationException>(() => JwtOptions.Resolve(Config(key), Production));
        }

        [Fact]
        public void Resolve_OutsideDevelopment_ThrowsWhenKeyIsTooShortForHmacSha256()
        {
            // HMAC-SHA256 needs a 256-bit key; anything shorter would fail at signing time instead,
            // long after startup.
            var shortKey = new string('k', JwtOptions.MinimumKeyBytes - 1);

            var ex = Assert.Throws<InvalidOperationException>(() => JwtOptions.Resolve(Config(shortKey), Production));
            Assert.Contains(JwtOptions.MinimumKeyBytes.ToString(), ex.Message);
        }

        /// <summary>
        /// The specific key that shipped in git history must never be usable in a real environment, even
        /// though it is long enough to pass the length check.
        /// </summary>
        [Fact]
        public void Resolve_OutsideDevelopment_ThrowsWhenKeyIsTheLeakedLegacyDefault()
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => JwtOptions.Resolve(Config(LeakedLegacyKey), Production));

            Assert.Contains("no longer secret", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Resolve_OutsideDevelopment_SucceedsWithAValidKey()
        {
            var result = JwtOptions.Resolve(Config(ValidKey), Production);

            Assert.Equal(ValidKey, result.Options.Key);
            Assert.Null(result.Warning);
        }

        [Fact]
        public void Resolve_InDevelopment_FallsBackToAGeneratedKeyAndWarns()
        {
            var result = JwtOptions.Resolve(Config(null), Development);

            Assert.False(string.IsNullOrWhiteSpace(result.Options.Key));
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(result.Options.Key) >= JwtOptions.MinimumKeyBytes);
            Assert.NotNull(result.Warning);
        }

        /// <summary>
        /// The development fallback must be generated per process, not a constant — otherwise it is just
        /// the committed-secret problem again with a different literal.
        /// </summary>
        [Fact]
        public void Resolve_InDevelopment_GeneratesADifferentKeyEachTime()
        {
            var first = JwtOptions.Resolve(Config(null), Development);
            var second = JwtOptions.Resolve(Config(null), Development);

            Assert.NotEqual(first.Options.Key, second.Options.Key);
        }

        [Fact]
        public void Resolve_InDevelopment_StillPrefersAnExplicitlyConfiguredKey()
        {
            var result = JwtOptions.Resolve(Config(ValidKey), Development);

            Assert.Equal(ValidKey, result.Options.Key);
            Assert.Null(result.Warning);
        }

        /// <summary>
        /// Issuer and audience were previously literals repeated in Program.cs and twice in
        /// AuthController. If any copy drifted, every token would silently fail validation.
        /// </summary>
        [Fact]
        public void IssuerAndAudience_AreSharedConstants()
        {
            Assert.Equal("Imperial2030Server", JwtOptions.Issuer);
            Assert.Equal("Imperial2030Client", JwtOptions.Audience);
        }

        // The plaintext is required here to prove the rejection actually fires, and it is already
        // permanently in this repository's git history, so nothing is disclosed by naming it. Production
        // code deliberately does NOT contain it — JwtOptions matches against a SHA-256 hash instead, so
        // the compromised value is not reintroduced into a shipped binary.
        private const string LeakedLegacyKey = "ThisIsASecretKeyForImperial2030GameOnly!";
    }
}
