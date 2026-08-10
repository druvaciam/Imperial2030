using System.Linq;
using Imperial2030.Server.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Microsoft.Data.Sqlite;
using System.Data.Common;

namespace Imperial2030.Tests
{
    public class CustomWebApplicationFactory<TStartup>
        : WebApplicationFactory<TStartup> where TStartup : class
    {
        private DbConnection _keepAliveConnection;
        private readonly string _dbName;

        public CustomWebApplicationFactory()
        {
            // Use a uniquely named in-memory database with a shared cache.
            // This allows multiple DbContexts on different threads to connect to the 
            // exact same in-memory database by opening their own connections.
            _dbName = Guid.NewGuid().ToString();
            var connectionString = $"DataSource=file:{_dbName}?mode=memory&cache=shared";
            
            // Keep one connection open to prevent SQLite from destroying the DB
            _keepAliveConnection = new SqliteConnection(connectionString);
            _keepAliveConnection.Open();
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _keepAliveConnection?.Dispose();
            }
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                var descriptors = services.Where(
                    d => d.ServiceType == typeof(DbContextOptions<ApplicationDbContext>) ||
                         d.ServiceType == typeof(DbContextOptions) ||
                         d.ServiceType.Name.Contains("DbConnection") ||
                         d.ServiceType.Name.Contains("DbContextOptions")).ToList();

                foreach (var descriptor in descriptors)
                {
                    services.Remove(descriptor);
                }

                // Remove background HostedServices to prevent concurrency/locking issues in tests
                var hostedServices = services.Where(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)).ToList();
                foreach (var hostedService in hostedServices)
                {
                    services.Remove(hostedService);
                }

                services.AddDbContext<ApplicationDbContext>(options =>
                {
                    // Pass connection string, not the DbConnection object! 
                    // This allows EF Core to pool/manage its own thread-safe connections.
                    options.UseSqlite($"DataSource=file:{_dbName}?mode=memory&cache=shared");
                    options.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
                });

                var sp = services.BuildServiceProvider();

                using (var scope = sp.CreateScope())
                {
                    var scopedServices = scope.ServiceProvider;
                    var db = scopedServices.GetRequiredService<ApplicationDbContext>();
                    var logger = scopedServices
                        .GetRequiredService<ILogger<CustomWebApplicationFactory<TStartup>>>();

                    db.Database.EnsureCreated();
                }

                // Add mock authentication
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "Test";
                    options.DefaultChallengeScheme = "Test";
                })
                .AddScheme<AuthenticationSchemeOptions, IntegrationTestAuthHandler>(
                    "Test", options => { });

                // We don't remove other auth schemes because they might be registered,
                // but setting default scheme to "Test" isn't strictly necessary if we use policies that don't hardcode schemes,
                // however we will configure authorization globally to allow any authenticated user in the test.
            });
        }
    }
}
