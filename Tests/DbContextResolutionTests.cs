using Imperial2030.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Imperial2030.Tests
{
    public class DbContextResolutionTests
    {
        [Fact]
        public void SqliteRegistration_ResolvesApplicationDbContextViaScope()
        {
            var services = new ServiceCollection();
            services.AddDbContext<SqliteApplicationDbContext>(opt =>
                opt.UseSqlite("Data Source=:memory:"));
            services.AddScoped<ApplicationDbContext>(sp => sp.GetRequiredService<SqliteApplicationDbContext>());

            var serviceProvider = services.BuildServiceProvider();
            using var scope = serviceProvider.CreateScope();

            var baseContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var sqliteContext = scope.ServiceProvider.GetRequiredService<SqliteApplicationDbContext>();

            Assert.NotNull(baseContext);
            Assert.NotNull(sqliteContext);
            Assert.Same(baseContext, sqliteContext);
        }

        [Fact]
        public void SqlServerRegistration_ResolvesApplicationDbContextViaScope()
        {
            var services = new ServiceCollection();
            services.AddDbContext<SqlServerApplicationDbContext>(opt =>
                opt.UseSqlServer("Server=localhost;Database=ImperialTest;Trusted_Connection=True;TrustServerCertificate=True;"));
            services.AddScoped<ApplicationDbContext>(sp => sp.GetRequiredService<SqlServerApplicationDbContext>());

            var serviceProvider = services.BuildServiceProvider();
            using var scope = serviceProvider.CreateScope();

            var baseContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var sqlServerContext = scope.ServiceProvider.GetRequiredService<SqlServerApplicationDbContext>();

            Assert.NotNull(baseContext);
            Assert.NotNull(sqlServerContext);
            Assert.Same(baseContext, sqlServerContext);
        }
    }
}
