using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using System.Security.Claims;
using Imperial2030.Shared.Models;
using Imperial2030.Shared.Constants;
using Imperial2030.Server.Configuration;
using NLog.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);

// Every ILogger call anywhere in the app (TcpTrainingServer, BotService, controllers, etc.) now also goes
// to a rolling log file under logs/ — no call-site changes needed anywhere else. Console keeps the default
// ASP.NET Core formatter (the classic "info: Category[0]" / indented-message look); NLog is added as an
// additional provider that only writes to file (see nlog.config — its own config has no console target,
// so this doesn't produce duplicate console lines in a different format).
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddNLog();

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
builder.Services.AddScoped<Imperial2030.Server.Services.GameReplayService>();
// Caps are bound from configuration so an operator can retune them (or open them back up) with an
// environment variable and a restart, rather than a rebuild and redeploy. That matters because the
// failure mode is user-visible: too low a per-caller cap refuses real people with "You already have
// the maximum number of replay sessions open". Omitted keys keep the defaults on the class.
builder.Services.AddSingleton(sp =>
{
    var manager = new Imperial2030.Server.Services.ReplaySessionManager(
        sp.GetRequiredService<IServiceScopeFactory>(),
        sp.GetRequiredService<ILogger<Imperial2030.Server.Services.ReplaySessionManager>>());

    var configured = builder.Configuration.GetValue<int?>("Replay:MaxConcurrentSessions");
    if (configured is > 0) manager.MaxConcurrentSessions = configured.Value;

    var perOwner = builder.Configuration.GetValue<int?>("Replay:MaxSessionsPerOwner");
    if (perOwner is > 0) manager.MaxSessionsPerOwner = perOwner.Value;

    var idleMinutes = builder.Configuration.GetValue<int?>("Replay:IdleTimeoutMinutes");
    if (idleMinutes is > 0) manager.IdleTimeout = TimeSpan.FromMinutes(idleMinutes.Value);

    return manager;
});
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
            
        builder.Services.AddDbContext<Imperial2030.Server.Data.SqliteApplicationDbContext>(opt => 
            opt.UseSqlite(dbPath, b => b.MigrationsAssembly(typeof(Imperial2030.Server.Data.SqliteApplicationDbContext).Assembly.FullName))
               .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));
        builder.Services.AddScoped<Imperial2030.Server.Data.ApplicationDbContext>(sp => sp.GetRequiredService<Imperial2030.Server.Data.SqliteApplicationDbContext>());
    }
    else
    {
        // Use SQL Server for standard connection strings (e.g., Azure App Service, local development)
        builder.Services.AddDbContext<Imperial2030.Server.Data.SqlServerApplicationDbContext>(opt => 
            opt.UseSqlServer(connectionString, b => b.MigrationsAssembly(typeof(Imperial2030.Server.Data.SqlServerApplicationDbContext).Assembly.FullName))
               .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));
        builder.Services.AddScoped<Imperial2030.Server.Data.ApplicationDbContext>(sp => sp.GetRequiredService<Imperial2030.Server.Data.SqlServerApplicationDbContext>());
    }

builder.Services.AddIdentity<Imperial2030.Server.Models.ApplicationUser, Microsoft.AspNetCore.Identity.IdentityRole>(
        Imperial2030.Server.Configuration.AuthSecurity.ConfigureLockout)
    .AddEntityFrameworkStores<Imperial2030.Server.Data.ApplicationDbContext>()
    .AddDefaultTokenProviders();

// Endpoint rate-limit policies (auth, replay). Applied via [EnableRateLimiting] on the specific
// endpoints rather than globally, so gameplay traffic is never throttled — see RateLimitPolicies for
// the partitioning caveat behind reverse proxies.
builder.Services.AddAppRateLimiting(builder.Configuration);

// Resolved once here and registered as a singleton so token ISSUANCE (AuthController) and token
// VALIDATION (below) are guaranteed to use the same key, issuer, audience and lifetime. These were
// previously duplicated literals in both files, each with its own hardcoded signing-key fallback.
// Throws outside Development when Jwt:Key is missing, too short, or set to the key that leaked into
// this repository's git history — see JwtOptions.
var (jwtOptions, jwtWarning) = Imperial2030.Server.Configuration.JwtOptions.Resolve(builder.Configuration, builder.Environment);
builder.Services.AddSingleton(jwtOptions);

// Only the Development no-key path returns a warning, and it is the path that mints a fresh signing key
// per process — which is exactly why yesterday's browser token stops validating after a restart.
bool usingEphemeralSigningKey = jwtWarning != null;

// Backs UserExistenceCache, which keeps OnTokenValidated's "does this user still exist" check off the
// user store on every authenticated request.
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<Imperial2030.Server.Services.UserExistenceCache>();

// Scans the deployment directory for exported .onnx bot models exactly once, instead of on every
// AddBot and every anonymous hit on the available-bots endpoint.
builder.Services.AddSingleton<Imperial2030.Server.Services.BotTypeCatalog>();

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
            ValidIssuer = Imperial2030.Server.Configuration.JwtOptions.Issuer,
            ValidAudience = Imperial2030.Server.Configuration.JwtOptions.Audience,
            IssuerSigningKey = jwtOptions.SigningKey
        };
        options.Events = new JwtBearerEvents
        {
            OnAuthenticationFailed = context =>
            {
                var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Imperial2030.Auth");

                // A token that fails to validate is a property of the REQUEST, not a fault in the server:
                // expired, stale, or simply not ours. Logged without the exception object (so no stack
                // trace) and below Warning, matching what the JwtBearer middleware itself does — the
                // request is already being rejected with 401, which is the actual outcome that matters.
                if (usingEphemeralSigningKey &&
                    context.Exception is SecurityTokenSignatureKeyNotFoundException or SecurityTokenInvalidSignatureException)
                {
                    logger.LogInformation(
                        "Rejected a token signed with a different key. This process generated an ephemeral " +
                        "Jwt:Key at startup (see the warning above), so any token issued before the last " +
                        "restart no longer validates. Sign in again, or set a stable key to keep local " +
                        "sessions across restarts.");
                }
                else
                {
                    logger.LogInformation("Token validation failed: {Reason}", context.Exception.Message);
                }
                return Task.CompletedTask;
            },
            OnChallenge = context =>
            {
                context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Imperial2030.Auth")
                    .LogInformation("Token challenge: {Error}, {ErrorDescription}", context.Error, context.ErrorDescription);
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
                // Guest principals (AuthController.GuestLogin) are backed by no ApplicationUser row at
                // all — by design: a guest is a throwaway identity, minted with a random NameIdentifier
                // so it can browse and spectate without registering. Running the store lookup below on
                // one always came back null and failed the token, so the server rejected its own
                // freshly-issued guest tokens with 401 on every [Authorize] endpoint, which in turn made
                // GamesController's eight `User.IsInRole("Guest")` checks unreachable dead code.
                // Gate on the same role claim those checks use, so the two can never disagree.
                //
                // Not a weakening of the existence check: minting a token with this role still requires
                // the signing key, and a real user who has since been deleted carries no Guest role and
                // is still rejected below.
                if (context.Principal?.IsInRole(GameConstants.GuestRole) == true) return;

                var userId = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
                if (userId != null)
                {
                    // Memoised for UserExistenceCache.DefaultTtl rather than hitting the user store on
                    // every single authenticated request - the replay view polls every 400ms, and each
                    // poll was paying for its own lookup. See UserExistenceCache for the trade-off.
                    var existenceCache = context.HttpContext.RequestServices.GetRequiredService<Imperial2030.Server.Services.UserExistenceCache>();
                    var userManager = context.HttpContext.RequestServices.GetRequiredService<UserManager<Imperial2030.Server.Models.ApplicationUser>>();

                    bool exists = await existenceCache.ExistsAsync(userId, async id => await userManager.FindByIdAsync(id) != null);
                    if (!exists)
                    {
                        context.Fail("User no longer exists.");
                    }
                }
            }
        };
    });
builder.Services.AddAuthorization(options =>
{
    // Refuses the Guest role outright. Guests already could not complete a maneuver (JoinGame turns them
    // away, so they never become a Player and each handler's controller-identity check fails), but that
    // left ManeuverController's safety resting entirely on an indirect argument.
    options.AddPolicy(GameConstants.NotGuestPolicy, policy => policy
        .RequireAuthenticatedUser()
        .RequireAssertion(context => !context.User.IsInRole(GameConstants.GuestRole)));
});

var app = builder.Build();

// Deferred from JwtOptions.Resolve, which runs before the logging pipeline exists.
if (jwtWarning != null)
{
    app.Logger.LogWarning("{JwtWarning}", jwtWarning);
}

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

// After UseRouting so the endpoint's [EnableRateLimiting] metadata has been resolved, and before
// UseAuthentication so throttled requests are rejected without doing token or password work first.
app.UseRateLimiter();

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
