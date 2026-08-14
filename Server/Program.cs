using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using System.Security.Claims;
using Imperial2030.Shared.Models;

var builder = WebApplication.CreateBuilder(args);

// Only register Windows Service hosting on Windows (skipped on Linux VPS deployments)
if (OperatingSystem.IsWindows())
    builder.Host.UseWindowsService();

// Add services to the container.

builder.Services.AddControllersWithViews();
builder.Services.AddRazorPages();
builder.Services.AddSignalR();
builder.Services.AddSingleton<Imperial2030.Server.Services.PresenceTracker>();
builder.Services.AddSingleton<Imperial2030.Server.Services.Bots.IBotStrategy, Imperial2030.Server.Services.Bots.Strategies.DefaultBotStrategy>();
builder.Services.AddSingleton<Imperial2030.Server.Services.Bots.IBotStrategy, Imperial2030.Server.Services.Bots.Strategies.AggressiveBotStrategy>();
builder.Services.AddSingleton<Imperial2030.Server.Services.Bots.IBotStrategy, Imperial2030.Server.Services.Bots.Strategies.FriendlyBotStrategy>();
builder.Services.AddSingleton<Imperial2030.Server.Services.Bots.IBotStrategy, Imperial2030.Server.Services.Bots.Strategies.GreedyBotStrategy>();
builder.Services.AddSingleton<Imperial2030.Server.Services.Bots.IBotStrategy, Imperial2030.Server.Services.Bots.Strategies.RandomBotStrategy>();
builder.Services.AddSingleton<Imperial2030.Server.Services.BotService>();
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

var isTrainingMode = args.Contains("--training");

if (isTrainingMode)
{
    builder.Services.AddSingleton<Imperial2030.Server.Services.INotificationService, Imperial2030.Server.Services.NoOpNotificationService>();
}
else
{
    builder.Services.AddHttpClient<Imperial2030.Server.Services.INotificationService, Imperial2030.Server.Services.NotificationService>();
}

if (isTrainingMode)
{
    builder.Services.AddHostedService<Imperial2030.Server.Services.TcpTrainingServer>();
}

    if (isTrainingMode)
    {
        builder.Services.AddDbContext<Imperial2030.Server.Data.ApplicationDbContext>(opt => opt.UseInMemoryDatabase("TrainingDB"));
    }
    else if (string.IsNullOrEmpty(connectionString) || connectionString.Contains(".db"))
    {
        // Use SQLite if no connection string is provided, or if it points to a .db file
        var dbPath = string.IsNullOrEmpty(connectionString) 
            ? $"Data Source={System.IO.Path.Combine(builder.Environment.ContentRootPath, "imperial2030.db")}"
            : connectionString;
            
        builder.Services.AddDbContext<Imperial2030.Server.Data.ApplicationDbContext, Imperial2030.Server.Data.SqliteApplicationDbContext>(opt => 
            opt.UseSqlite(dbPath, b => b.MigrationsAssembly(typeof(Imperial2030.Server.Data.SqliteApplicationDbContext).Assembly.FullName))
               .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));
    }
    else
    {
        // Use SQL Server for standard connection strings (e.g., Azure App Service, local development)
        builder.Services.AddDbContext<Imperial2030.Server.Data.ApplicationDbContext, Imperial2030.Server.Data.SqlServerApplicationDbContext>(opt => 
            opt.UseSqlServer(connectionString, b => b.MigrationsAssembly(typeof(Imperial2030.Server.Data.SqlServerApplicationDbContext).Assembly.FullName))
               .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));
    }

builder.Services.AddIdentity<Imperial2030.Server.Models.ApplicationUser, Microsoft.AspNetCore.Identity.IdentityRole>()
    .AddEntityFrameworkStores<Imperial2030.Server.Data.ApplicationDbContext>()
    .AddDefaultTokenProviders();

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
})
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = "Imperial2030Server",
            ValidAudience = "Imperial2030Client",
            // JWT key can be overridden via config/env var (e.g. Jwt__Key on Linux VPS)
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(
                builder.Configuration["Jwt:Key"] ?? "ThisIsASecretKeyForImperial2030GameOnly!"))
        };
        options.Events = new JwtBearerEvents
        {
            OnAuthenticationFailed = context =>
            {
                Console.WriteLine($"Token validation failed: {context.Exception.Message}");
                return Task.CompletedTask;
            },
            OnChallenge = context =>
            {
                Console.WriteLine($"Token challenge: {context.Error}, {context.ErrorDescription}");
                return Task.CompletedTask;
            },
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/gamehub"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            },
            OnTokenValidated = async context =>
            {
                var userManager = context.HttpContext.RequestServices.GetRequiredService<UserManager<Imperial2030.Server.Models.ApplicationUser>>();
                var userId = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
                if (userId != null)
                {
                    var user = await userManager.FindByIdAsync(userId);
                    if (user == null)
                    {
                        context.Fail("User no longer exists.");
                    }
                }
            }
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();


app.MapRazorPages();
app.MapControllers();
app.MapHub<Imperial2030.Server.Hubs.GameHub>("/gamehub");
app.MapFallbackToFile("index.html");

using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    try
    {
        await Imperial2030.Server.Data.DbSeeder.SeedAsync(services);
        var logger = services.GetRequiredService<ILogger<Program>>();
        var dbContext = services.GetRequiredService<Imperial2030.Server.Data.ApplicationDbContext>();

        var twoWeeksAgo = DateTime.UtcNow.AddDays(-14);
        var statuses = new[] { GameStatus.Lobby, GameStatus.InProgress };
        var oldGames = await dbContext.Games
            .Where(g => statuses.Contains(g.Status) && g.CreatedAt < twoWeeksAgo)
            .ToListAsync();
        
        if (oldGames.Any())
        {
            dbContext.Games.RemoveRange(oldGames);
            await dbContext.SaveChangesAsync();
            logger.LogInformation($"Cleaned up {oldGames.Count} old in-progress/lobby games created before {twoWeeksAgo}.");
        }

        var finishedGamesWithoutWinner = await dbContext.Games
            .Where(g => g.Status == GameStatus.Finished && (g.WinnerName == null || g.WinnerName == ""))
            .ToListAsync();

        if (finishedGamesWithoutWinner.Any())
        {
            logger.LogInformation($"Backfilling WinnerName for {finishedGamesWithoutWinner.Count} finished games...");
            
            foreach (var g in finishedGamesWithoutWinner)
            {
                await Imperial2030.Server.Helpers.GameHelper.SetWinnerNameAsync(g, dbContext);
            }
            await dbContext.SaveChangesAsync();
        }
    }
    catch (Exception ex)
    {
        var logger = services.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "An error occurred creating the DB or cleaning up old games.");
    }
}

app.Run();

public partial class Program { }
