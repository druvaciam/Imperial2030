using System.Linq;
using Imperial2030.Server.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Imperial2030.Tests
{
    /// <summary>
    /// Like <see cref="CustomWebApplicationFactory{TStartup}"/>, but deliberately leaves the real
    /// authentication pipeline in place.
    ///
    /// CustomWebApplicationFactory registers IntegrationTestAuthHandler as the default authenticate and
    /// challenge scheme, which means no test using it ever exercises JWT validation, Program.cs's
    /// JwtBearerEvents (OnMessageReceived / OnTokenValidated), role claims, or token expiry. That blind
    /// spot is why a total outage of guest login — every guest token being rejected with 401 by the
    /// OnTokenValidated user-store lookup — sat undetected in a green test suite.
    ///
    /// Only the database is swapped here (to a per-factory InMemory store); everything auth-related is
    /// the production configuration. Jwt:Key is supplied through configuration exactly as a real
    /// deployment supplies it, so this also covers the startup key validation.
    /// </summary>
    public class RealAuthWebApplicationFactory<TStartup> : WebApplicationFactory<TStartup> where TStartup : class
    {
        /// <summary>Test signing key. Must be >= 32 chars to satisfy Program.cs's startup validation.</summary>
        public const string TestJwtKey = "IntegrationTestSigningKey_ForImperial2030_AtLeast32Chars!";

        private readonly string _dbName = $"RealAuthDb_{System.Guid.NewGuid()}";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Jwt:Key", TestJwtKey);

            builder.ConfigureServices(services =>
            {
                var descriptors = services.Where(
                    d => d.ServiceType == typeof(ApplicationDbContext) ||
                         d.ServiceType == typeof(SqlServerApplicationDbContext) ||
                         d.ServiceType == typeof(SqliteApplicationDbContext) ||
                         d.ServiceType == typeof(DbContextOptions<ApplicationDbContext>) ||
                         d.ServiceType == typeof(DbContextOptions) ||
                         d.ServiceType == typeof(Imperial2030.Server.Services.INotificationService) ||
                         d.ServiceType.Name.Contains("DbConnection") ||
                         d.ServiceType.Name.Contains("DbContextOptions")).ToList();

                foreach (var descriptor in descriptors)
                {
                    services.Remove(descriptor);
                }

                services.AddDbContext<ApplicationDbContext>(options => options.UseInMemoryDatabase(_dbName));
                services.AddSingleton<Imperial2030.Server.Services.INotificationService,
                                      Imperial2030.Server.Services.NoOpNotificationService>();
            });
        }
    }
}
