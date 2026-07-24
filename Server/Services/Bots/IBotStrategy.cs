using Imperial2030.Shared.Models;
using Imperial2030.Server.Models;
using Imperial2030.Shared.Constants;

namespace Imperial2030.Server.Services.Bots;

public class PendingBattle
{
    public string TerritoryId { get; set; } = "";
    public Imperial2030.Shared.Models.Nation AggressorNation { get; set; }
    public List<Imperial2030.Shared.Models.Nation> DefenderNations { get; set; } = new();
}

public interface IBotStrategy
{
    string Name { get; }
    
    // Core decision weighting
    double ScoreRondelSlot(int slot, Game game, NationState ns, Player controller, int factories, int units);
    
    // Specific actions
    Bond? ChooseBondToBuy(Game game, Player actor, List<Nation> controlledNations, List<Bond> availableBonds);
    
    string? ChooseCityForFactory(Game game, Nation nation, List<Territory> validCities);
    
    List<(UnitType Type, string TerritoryId)> ChooseImports(Game game, NationState ns, int maxImport, List<Territory> homeTerritories);
    
    double ScoreManeuverDestination(Game game, Unit unit, string destinationId, Player controller);
    
    bool RetreatFromBattle(Game game, PendingBattle battle);
}
