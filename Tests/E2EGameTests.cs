using System.Net.Http.Json;
using System.Net.Http.Headers;
using Imperial2030.Shared.Models;
using Imperial2030.Server.Data;
using Imperial2030.Server.Models;
using Imperial2030.Server.Services.Bots;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Imperial2030.Tests
{
    public class E2EGameTests : IClassFixture<CustomWebApplicationFactory<Program>>
    {
        private readonly CustomWebApplicationFactory<Program> _factory;
        private readonly ITestOutputHelper _output;

        public E2EGameTests(CustomWebApplicationFactory<Program> factory, ITestOutputHelper output)
        {
            _factory = factory;
            _output = output;
        }

        [Fact]
        public async Task TestFullGameViaUrls()
        {
            var client = _factory.CreateClient();
            var p1Id = Guid.NewGuid().ToString();
            var p2Id = Guid.NewGuid().ToString();

            // 1. Create Game as Player 1
            client.DefaultRequestHeaders.Add("X-Test-User", p1Id);
            var gameName = "E2E_Test_Game";
            var createReq = new { Name = gameName, IsVariantActive = true };
            var createRes = await client.PostAsJsonAsync("/api/games", createReq);
            createRes.EnsureSuccessStatusCode();
            var gameDto = await createRes.Content.ReadFromJsonAsync<GameDto>();
            var gameId = gameDto.Id;

            // 2. Join Game as Player 2
            client.DefaultRequestHeaders.Remove("X-Test-User");
            client.DefaultRequestHeaders.Add("X-Test-User", p2Id);
            var joinRes = await client.PostAsync($"/api/games/{gameId}/join", null);
            joinRes.EnsureSuccessStatusCode();

            // 3. Player 1 Starts Game
            client.DefaultRequestHeaders.Remove("X-Test-User");
            client.DefaultRequestHeaders.Add("X-Test-User", p1Id);
            var startRes = await client.PostAsync($"/api/games/{gameId}/start", null);
            startRes.EnsureSuccessStatusCode();

            int turnCount = 0;
            // 5. Game Loop
            while (turnCount < 2000)
            {
                var stateRes = await client.GetAsync($"/api/games/{gameId}");
                if (!stateRes.IsSuccessStatusCode) break;

                var state = await stateRes.Content.ReadFromJsonAsync<GameDto>();
                if (state == null || state.Status == GameStatus.Finished)
                {
                    _output.WriteLine($"Game finished in {turnCount} actions.");
                    break;
                }

                // Remove the activeUser check here, we'll determine it after querying the DB.
                // We will just query the game first.

                using var scope = _factory.Services.CreateScope();
                var ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var botStrategies = scope.ServiceProvider.GetRequiredService<IEnumerable<IBotStrategy>>();
                var strategy = botStrategies.FirstOrDefault(s => s.Name == "Default") ?? botStrategies.First();

                var game = await ctx.Games
                    .Include(g => g.NationStates)
                    .Include(g => g.TerritoryStates)
                    .Include(g => g.Units)
                    .Include(g => g.Players)
                    .Include(g => g.Bonds)
                    .AsSplitQuery()
                    .FirstOrDefaultAsync(g => g.Id == gameId);

                if (game == null) break;

                var currentNation = game.CurrentTurnNation;
                var ns = game.NationStates.FirstOrDefault(n => n.Nation == currentNation);
                var activeUser = game.IsInvestorTurn && game.ActingPlayerId.HasValue
                    ? game.Players.FirstOrDefault(p => p.Id == game.ActingPlayerId)?.UserId
                    : game.Players.FirstOrDefault(p => p.Id == ns?.ControllerId)?.UserId;

                if (string.IsNullOrEmpty(activeUser))
                {
                    await Task.Delay(10);
                    continue; // e.g. waiting for someone to respond, fallback
                }

                client.DefaultRequestHeaders.Remove("X-Test-User");
                client.DefaultRequestHeaders.Add("X-Test-User", activeUser);

                var controllerId = ns?.ControllerId ?? game.ActingPlayerId;
                var controller = game.Players.FirstOrDefault(p => p.Id == controllerId);
                var actor = game.Players.FirstOrDefault(p => p.UserId == activeUser);

                if (controller == null) controller = actor;
                if (controller == null) break; // Should not happen

                // Execute move depending on phase
                if (game.IsInvestorTurn)
                {
                    if (game.PendingSwissBankForceNation != null && game.PendingSwissBankResponders.Contains(controller.Id))
                    {
                        var req = new { TradeInBondNation = (Nation?)null, PurchasedBondNation = (Nation?)null };
                        var res = await client.PostAsJsonAsync($"/api/games/{gameId}/swissbank-response", req);
                        res.EnsureSuccessStatusCode();
                    }
                    else
                    {
                        var req = new { ActionType = "Pass", TargetBondId = (Guid?)null, TradeInBondId = (Guid?)null };
                        var res = await client.PostAsJsonAsync($"/api/games/{gameId}/investor-action", req);
                        res.EnsureSuccessStatusCode();
                    }
                }
                else if (game.PendingBattleDefenders.Any())
                {
                    var res = await client.PostAsJsonAsync($"/api/maneuver/{gameId}/battle-response", new { Retreat = false });
                    res.EnsureSuccessStatusCode();
                }
                else if (!ns.HasMovedThisTurn)
                {
                    int bestSlot = 0;
                    double bestScore = -1000;
                    int currentSlot = ns.RondelPosition ?? -1;

                    var validSlots = new List<int>();
                    if (currentSlot == -1) validSlots.AddRange(new[] { 0, 1, 2, 3, 4, 5, 6, 7 });
                    else
                    {
                        for (int i = 1; i <= 3; i++) validSlots.Add((currentSlot + i) % 8);
                    }

                    foreach (var slot in validSlots)
                    {
                        var score = strategy.ScoreRondelSlot(slot, game, ns, controller,
                            game.TerritoryStates.Count(t => t.HasFactory && Imperial2030.Shared.Constants.TerritoryData.AllTerritories.FirstOrDefault(x => x.Id == t.TerritoryId)?.Nation == currentNation),
                            game.Units.Count(u => u.Nation == currentNation));

                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestSlot = slot;
                        }
                    }
                    var res = await client.PostAsync($"/api/games/{gameId}/move/{currentNation}/{bestSlot}", null);
                    if (!res.IsSuccessStatusCode) throw new Exception(await res.Content.ReadAsStringAsync());
                }
                else if (ns.HasMovedThisTurn && ns.RondelPosition.HasValue)
                {
                    int pos = ns.RondelPosition.Value;
                    bool shouldEndTurn = false;

                    if (pos == 5) // Import
                    {
                        if (!ns.HasImportedThisTurn)
                        {
                            int maxImport = Math.Min(3, ns.Treasury);
                            var homeTerritories = Imperial2030.Shared.Constants.TerritoryData.AllTerritories.Where(t => t.Nation == currentNation).ToList();
                            var imports = strategy.ChooseImports(game, ns, maxImport, homeTerritories);

                            if (imports.Count > 0)
                            {
                                var unitsReq = imports.Select(i => new { UnitType = i.Type, TerritoryId = i.TerritoryId }).ToList();
                                var res = await client.PostAsJsonAsync($"/api/games/{gameId}/import", new { Units = unitsReq });
                                if (!res.IsSuccessStatusCode) throw new Exception(await res.Content.ReadAsStringAsync());
                            }
                            else
                            {
                                shouldEndTurn = true;
                            }
                        }
                        else
                            shouldEndTurn = true;
                    }
                    else if (pos == 2 || pos == 6) // Production
                    {
                        if (!ns.HasProducedThisTurn)
                        {
                            var res = await client.PostAsync($"/api/games/{gameId}/production", null);
                            if (!res.IsSuccessStatusCode) throw new Exception(await res.Content.ReadAsStringAsync());
                        }
                        else
                            shouldEndTurn = true;
                    }
                    else if (pos == 1) // Factory
                    {
                        if (!ns.HasBuiltThisTurn)
                        {
                            var validCities = game.TerritoryStates.Where(t => !t.HasFactory && Imperial2030.Shared.Constants.TerritoryData.AllTerritories.FirstOrDefault(x => x.Id == t.TerritoryId)?.Nation == currentNation).Select(t => new Territory { Id = t.TerritoryId, Name = t.TerritoryId }).ToList();
                            var chosenCity = strategy.ChooseCityForFactory(game, currentNation, validCities);
                            var res = await client.PostAsync($"/api/games/{gameId}/build-factory/{chosenCity}", null);
                            res.EnsureSuccessStatusCode();
                        }
                        else
                        {
                            shouldEndTurn = true;
                        }
                    }
                    else if (pos == 0) // Taxation
                    {
                        var res = await client.PostAsync($"/api/games/{gameId}/taxation", null);
                        if (!res.IsSuccessStatusCode) throw new Exception(await res.Content.ReadAsStringAsync());
                    }
                    else if (pos == 3 || pos == 7) // Maneuver
                    {
                        if (game.CurrentManeuverPhase == ManeuverPhase.Fleets || game.CurrentManeuverPhase == ManeuverPhase.Armies)
                        {
                            var res = await client.PostAsync($"/api/maneuver/{gameId}/next-phase", null);
                            if (!res.IsSuccessStatusCode) throw new Exception(await res.Content.ReadAsStringAsync());
                        }
                        else
                        {
                            shouldEndTurn = true;
                        }
                    }
                    else if (pos == 4) // Investor
                    {
                        shouldEndTurn = true;
                    }

                    if (shouldEndTurn)
                    {
                        var res = await client.PostAsync($"/api/games/{gameId}/end-turn", null);
                        if (!res.IsSuccessStatusCode) throw new Exception(await res.Content.ReadAsStringAsync());
                    }
                }

                turnCount++;
            }

            // Assert
            var finalStateRes = await client.GetAsync($"/api/games/{gameId}");
            var finalState = await finalStateRes.Content.ReadFromJsonAsync<GameDto>();
            Assert.NotNull(finalState);
            Assert.Equal(GameStatus.Finished, finalState.Status);
            Assert.NotNull(finalState.FinishedAt);
            Assert.True(finalState.CreatedAt < finalState.FinishedAt);
            Assert.False(string.IsNullOrEmpty(finalState.WinnerName));
            Assert.Contains(finalState.WinnerName, new[] { p1Id.ToString(), p2Id.ToString() });
        }
    }
}
