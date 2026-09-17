using Imperial2030.Server.Data;
using Imperial2030.Server.Models;
using Imperial2030.Server.Services;
using Imperial2030.Shared.Models;

namespace Imperial2030.Server.Helpers;

/// <summary>
/// The <see cref="GameDetailDto"/> projection of a fully loaded <see cref="Game"/>, shared by
/// <c>GamesController.GetGame</c> and the replay session's snapshots. <paramref name="presence"/> is
/// null for a replay session, where nobody is online in the game being replayed.
/// </summary>
public static class GameDetailDtoBuilder
{
    public static GameDetailDto Build(Game game, string? userId, ApplicationDbContext? context, PresenceTracker? presence)
    {
        return new GameDetailDto
        {
            Id = game.Id,
            Name = game.Name,
            Status = game.Status,
            CreatedAt = game.CreatedAt,
            FinishedAt = game.FinishedAt,
            WinnerName = game.WinnerName,
            IsPrivate = game.IsPrivate,
            IsPaused = game.IsPaused,
            VariantBonusOnlyForTaxIncreases = game.VariantBonusOnlyForTaxIncreases,
            IsCurrentUserInGame = userId != null && game.Players.Any(p => p.UserId == userId),
            IsCurrentUserHost = userId != null && game.Players.Any(p => p.IsHost && p.UserId == userId),
            JoinCode = game.Players.Any(p => p.UserId == userId && p.IsHost) ? game.JoinCode : null,
            CurrentTurnNation = game.CurrentTurnNation,
            PlayerCount = game.Players.Count,
            // Seating order (Player.Id, as everywhere else). EF returns a collection in no fixed order and
            // the replay's in-memory copy changes it between polls; a roster that reshuffles under a
            // <select> leaves the browser showing one player's name for another's assets.
            Players = game.Players.GetOrderedPlayers().Select(p => new PlayerDto
            {
                Id = p.Id,
                UserId = p.IsBot ? $"bot-{p.Id}" : p.UserId!,
                // GetPlayerName checks BotName before IsBot: replay/import players are kept IsBot=false with
                // no backing ApplicationUser, and their display name is in BotName (see PlayerHelper).
                UserName = p.GetPlayerName(context),
                IsHost = p.IsHost,
                Cash = p.Cash,
                IsBot = p.IsBot,
                IsOnline = p.IsBot || (presence?.IsUserOnline(p.UserId) ?? false),
                IsActiveInGame = p.IsBot || (presence?.IsUserActiveInGame(game.Id.ToString(), p.UserId) ?? false),
                Bonds = game.Bonds.Where(b => b.HolderId == p.Id).Select(b => new BondDto
                {
                    Id = b.Id,
                    Nation = b.Nation,
                    Cost = b.Cost,
                    Interest = b.Interest,
                    HolderName = p.GetPlayerName(context)
                }).ToList()
            }).ToList(),
            NationStates = game.NationStates.Select(ns => new NationStateDto
            {
                Nation = ns.Nation,
                Treasury = ns.Treasury,
                Power = ns.Power,
                RondelPosition = ns.RondelPosition,
                // Same as UserName above. ControllerName must not be null during replay: the client's
                // IsMyTurn() compares it with MyPlayer?.UserName, which is null for the replay viewer, and
                // null == null would show action controls during playback.
                ControllerName = ns.Controller != null ? ns.Controller.GetPlayerName(context) : null,
                ControllerId = ns.ControllerId,
                HasBuiltThisTurn = ns.HasBuiltThisTurn,
                HasProducedThisTurn = ns.HasProducedThisTurn,
                HasMovedThisTurn = ns.HasMovedThisTurn,
                HasImportedThisTurn = ns.HasImportedThisTurn,
                TaxRevenue = ns.TaxRevenue,
                PreviousTaxRevenue = ns.PreviousTaxRevenue
            }).ToList(),
            AvailableBonds = game.Bonds.Where(b => b.HolderId == null).Select(b => new BondDto
            {
                Id = b.Id,
                Nation = b.Nation,
                Cost = b.Cost,
                Interest = b.Interest,
                HolderName = null
            }).ToList(),
            Territories = game.TerritoryStates.Select(ts => new TerritoryStateDto
            {
                TerritoryId = ts.TerritoryId,
                HasFactory = ts.HasFactory,
                Controller = ts.Controller
            }).ToList(),
            InvestorCardHolderId = game.InvestorCardHolderId,
            IsInvestorTurn = game.IsInvestorTurn,
            ActingPlayerId = game.ActingPlayerId,
            PendingBattleTerritoryId = game.PendingBattleTerritoryId,
            PendingBattleAggressorNation = game.PendingBattleAggressorNation,
            PendingBattleDefenders = game.PendingBattleDefenders.ToList(),
            PendingSwissBankForceNation = game.PendingSwissBankForceNation,
            PendingSwissBankResponders = game.PendingSwissBankResponders.ToList(),
            Units = game.Units.ToList(),
            ManeuverState = new ManeuverState { Phase = game.CurrentManeuverPhase },
            Actions = game.Actions.OrderBy(a => a.OrderIndex).ThenBy(a => a.Timestamp).Select(a => new GameActionDto
            {
                Id = a.Id,
                OrderIndex = a.OrderIndex,
                Timestamp = a.Timestamp,
                PlayerName = a.PlayerName,
                Nation = a.Nation,
                ActionType = a.ActionType,
                Message = a.Message,
                Metadata = a.Metadata ?? string.Empty
            }).ToList()
        };
    }
}
