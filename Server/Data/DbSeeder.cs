using Imperial2030.Server.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Imperial2030.Server.Data;

public static class DbSeeder
{
    public static async Task SeedAsync(IServiceProvider serviceProvider)
    {
        var context = serviceProvider.GetRequiredService<ApplicationDbContext>();
        // Apply any pending migrations (creates DB if not exists)
        if (!context.Database.IsRelational())
        {
            await context.Database.EnsureCreatedAsync();
        }
        else
        {
            await context.Database.MigrateAsync();
        }

        await SeedUsersAsync(serviceProvider);
    }

    private static async Task SeedUsersAsync(IServiceProvider serviceProvider)
    {
        var userManager = serviceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var logger = serviceProvider.GetRequiredService<ILogger<Program>>(); // Or own logger

        string[] players = { "player1", "player2", "player3", "player4", "player5", "player6" };

        foreach (var name in players)
        {
            if (await userManager.FindByNameAsync(name) == null)
            {
                var user = new ApplicationUser
                {
                    UserName = name,
                    Email = $"{name}@example.com"
                };
                var result = await userManager.CreateAsync(user, "Password123!");
                if (!result.Succeeded)
                {
                    logger.LogError($"Failed to create {name}: {string.Join(", ", result.Errors.Select(e => e.Description))}");
                }
                else
                {
                    logger.LogInformation($"Seeded user: {name}");
                }
            }
        }
    }
}
