using Imperial2030.Server.Models;
using Imperial2030.Shared.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Imperial2030.Server.Data;

public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options)
    {
    }

    public DbSet<Game> Games { get; set; } = default!;
    public DbSet<Player> Players { get; set; } = default!;
    public DbSet<Bond> Bonds { get; set; } = default!;
    public DbSet<NationState> NationStates { get; set; } = default!;
    public DbSet<TerritoryState> TerritoryStates { get; set; } = default!;
    public DbSet<Unit> Units { get; set; } = default!;
    public DbSet<GameAction> GameActions { get; set; } = default!;

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);

        configurationBuilder.Properties<Enum>().HaveConversion<string>();
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        // Any other specific configurations
    }
}
