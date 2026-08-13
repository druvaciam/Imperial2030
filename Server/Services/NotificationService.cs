using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Imperial2030.Server.Models;
using System.Linq;
using Imperial2030.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Imperial2030.Server.Data;
using System.Collections.Generic;

namespace Imperial2030.Server.Services
{
    public interface INotificationService
    {
        Task NotifyGameStartedAsync(Game game);
        Task NotifyGameFinishedAsync(Game game, string summary);
    }

    public class NotificationService : INotificationService
    {
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;
        private readonly ILogger<NotificationService> _logger;
        private readonly IServiceScopeFactory _scopeFactory;

        public NotificationService(HttpClient httpClient, IConfiguration configuration, ILogger<NotificationService> logger, IServiceScopeFactory scopeFactory)
        {
            _httpClient = httpClient;
            _configuration = configuration;
            _logger = logger;
            _scopeFactory = scopeFactory;
        }

        public async Task NotifyGameStartedAsync(Game game)
        {
            try
            {
                var functionUrl = _configuration["AzureFunctionNotificationUrl"];
                if (string.IsNullOrEmpty(functionUrl))
                {
                    _logger.LogWarning("AzureFunctionNotificationUrl is not configured.");
                    return;
                }

                List<string> playerEmails = new List<string>();
                string hostName = "Unknown";
                int playerCount = game.Players?.Count ?? 0;
                List<string> playerNames = new List<string>();

                using (var scope = _scopeFactory.CreateScope())
                {
                    var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    var fullGame = await context.Games
                        .Include(g => g.Players).ThenInclude(p => p.User)
                        .FirstOrDefaultAsync(g => g.Id == game.Id);

                    if (fullGame != null)
                    {
                        playerCount = fullGame.Players.Count;
                        hostName = fullGame.Players.FirstOrDefault(p => p.IsHost)?.User?.UserName ?? "Unknown";
                        
                        foreach (var player in fullGame.Players)
                        {
                            if (player.IsBot)
                            {
                                playerNames.Add(player.BotName ?? "Bot");
                            }
                            else if (player.User != null)
                            {
                                playerNames.Add(player.User.UserName ?? "Player");
                                if (!string.IsNullOrEmpty(player.User.Email))
                                {
                                    playerEmails.Add(player.User.Email);
                                }
                            }
                        }
                    }
                }

                var payload = new
                {
                    GameName = game.Name,
                    HostName = hostName,
                    PlayerCount = playerCount,
                    PlayerNames = playerNames,
                    VariantBonusOnlyForTaxIncreases = game.VariantBonusOnlyForTaxIncreases,
                    PlayerEmails = playerEmails,
                    StartedDate = game.StartedAt ?? game.CreatedAt
                };

                // Fire and forget
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var json = System.Text.Json.JsonSerializer.Serialize(payload);
                        var request = new HttpRequestMessage(HttpMethod.Post, $"{functionUrl.TrimEnd('/')}/api/GameStarted");
                        var functionKey = _configuration["AzureFunctionNotificationKey"];
                        if (!string.IsNullOrEmpty(functionKey))
                        {
                            request.Headers.Add("x-functions-key", functionKey);
                        }
                        request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                        var response = await _httpClient.SendAsync(request);
                        if (!response.IsSuccessStatusCode)
                        {
                            var errorContent = await response.Content.ReadAsStringAsync();
                            _logger.LogError($"Failed to send GameStarted notification. Status code: {response.StatusCode}. Details: {errorContent}");
                        }
                    }
                    catch (Exception ex)
                    {
                        try { _logger.LogError(ex, "Error calling GameStarted Azure Function."); } catch { }
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error preparing GameStarted notification.");
            }
        }

        public async Task NotifyGameFinishedAsync(Game game, string summary)
        {
            try
            {
                var functionUrl = _configuration["AzureFunctionNotificationUrl"];
                if (string.IsNullOrEmpty(functionUrl))
                {
                    _logger.LogWarning("AzureFunctionNotificationUrl is not configured.");
                    return;
                }

                List<string> playerEmails = new List<string>();
                List<string> playerNames = new List<string>();
                int playerCount = 0;
                string hostName = "Unknown";
                int winnerVP = 0;
                string? winnerEmail = null;

                using (var scope = _scopeFactory.CreateScope())
                {
                    var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    
                    // Fetch full player entities to get User and calculate VP
                    var fullGame = await context.Games
                        .Include(g => g.Players).ThenInclude(p => p.User)
                        .Include(g => g.NationStates)
                        .Include(g => g.Bonds)
                        .FirstOrDefaultAsync(g => g.Id == game.Id);

                    if (fullGame != null)
                    {
                        playerCount = fullGame.Players.Count;
                        hostName = fullGame.Players.FirstOrDefault(p => p.IsHost)?.User?.UserName ?? "Unknown";

                        foreach (var player in fullGame.Players)
                        {
                            if (player.IsBot)
                            {
                                playerNames.Add(player.BotName ?? "Bot");
                            }
                            else if (player.User != null)
                            {
                                playerNames.Add(player.User.UserName ?? "Player");
                                if (!string.IsNullOrEmpty(player.User.Email))
                                {
                                    playerEmails.Add(player.User.Email);
                                }
                            }
                        }

                        // Determine winner VP by calculating scores
                        var ranked = fullGame.GetRankedPlayers();
                        var winner = ranked.FirstOrDefault();
                        if (winner != null)
                        {
                            winnerVP = fullGame.CalculateScore(winner.Id);
                            if (!winner.IsBot && winner.User != null)
                            {
                                winnerEmail = winner.User.Email;
                            }
                        }
                    }
                }

                var payload = new
                {
                    GameName = game.Name,
                    WinnerName = game.WinnerName ?? "Unknown",
                    WinnerVP = winnerVP,
                    WinnerEmail = winnerEmail,
                    TurnsTaken = game.TurnCount,
                    StartedDate = game.StartedAt ?? game.CreatedAt,
                    FinishedDate = game.FinishedAt ?? DateTime.UtcNow,
                    HostName = hostName,
                    PlayerCount = playerCount,
                    PlayerNames = playerNames,
                    VariantBonusOnlyForTaxIncreases = game.VariantBonusOnlyForTaxIncreases,
                    Summary = summary,
                    PlayerEmails = playerEmails
                };

                // Fire and forget
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var json = System.Text.Json.JsonSerializer.Serialize(payload);
                        var request = new HttpRequestMessage(HttpMethod.Post, $"{functionUrl.TrimEnd('/')}/api/GameFinished");
                        var functionKey = _configuration["AzureFunctionNotificationKey"];
                        if (!string.IsNullOrEmpty(functionKey))
                        {
                            request.Headers.Add("x-functions-key", functionKey);
                        }
                        request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                        var response = await _httpClient.SendAsync(request);
                        if (!response.IsSuccessStatusCode)
                        {
                            var errorContent = await response.Content.ReadAsStringAsync();
                            _logger.LogError($"Failed to send GameFinished notification. Status code: {response.StatusCode}. Details: {errorContent}");
                        }
                    }
                    catch (Exception ex)
                    {
                        try { _logger.LogError(ex, "Error calling GameFinished Azure Function."); } catch { }
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error preparing GameFinished notification.");
            }
        }
    }
}
