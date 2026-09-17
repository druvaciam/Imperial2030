using Imperial2030.Server.Models;
using Imperial2030.Server.Helpers;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;

namespace Imperial2030.Server.Services.Bots.Strategies;

public class DefaultBotStrategy : BotStrategyBase
{
    private const int TaxPowerGainScoreWeight = 4;
    private const int TaxTreasuryGainScoreWeight = 2;
    private const int TaxBonusScoreWeight = 2;
    private const int ExceptionalTaxationScore = 200;
    private const int ProductionLandDangerBonus = 20;
    private const int ProductionNavalDangerBonus = 12;
    private const int OccupiedHomeImportBonus = 50;
    private const int LimitedProductionImportBonus = 20;
    private const int ImportDefenseDeficitBonus = 10;
    private const int MissingFactoryCapacityImportBonus = 20;
    private const int DirectThreatImportWeight = 500;
    private const int ReachOccupiedHomeImportWeight = 250;
    private const int ThreatenedUnitTypeImportWeight = 30;
    private const int BaseImportWeight = 10;
    private const int LowerUnitCountImportWeight = 10;
    private const int NeededHomeDefenderScoreBonus = 160;
    private const int MaximumEmergencyRondelScore = 150;
    private const int MaximumUnitsAboveFactoryCount = 3;
    private const double RondelSelectionTemperature = 10.0;
    private const string LondonTerritoryId = "London";

    public override string Name => "Default";

    public override double GetRondelSelectionWeight(double candidateScore, double highestCandidateScore) =>
        Math.Exp((candidateScore - highestCandidateScore) / RondelSelectionTemperature);

    public override double ScoreRondelSlot(int slot, Game game, NationState ns, Player controller, int factories, int units)
    {
        return slot switch
        {
            1 => (ns.Treasury >= 5 && CanBuildFactory(game, ns.Nation)) ? 25 : 0,       // Factory
            2 or 6 => units >= factories + MaximumUnitsAboveFactoryCount
                ? 0
                : ScoreProduction(game, ns, controller),                     // Production
            0 => ScoreTaxation(game, ns),                                      // Taxation
            3 or 7 => HasExpandableTargets(game, ns.Nation, controller) ? 15 : 0, // Maneuver
            5 => ScoreImport(game, ns, controller),                            // Import
            4 => 3,                                                    // Investor
            _ => 0
        };
    }

    public override bool RetreatFromBattle(Game game, PendingBattle battle)
    {
        return Random.Shared.Next(3) == 0;
    }

    public override double ScoreManeuverDestination(Game game, Unit unit, string destinationId, Player controller)
    {
        double score = base.ScoreManeuverDestination(game, unit, destinationId, controller);
        var destination = TerritoryData.AllTerritories.FirstOrDefault(territory => territory.Id == destinationId);
        if (unit.UnitType != UnitType.Army || destination?.Nation != unit.Nation) return score;

        var homeDefense = HomeDefenseHelper.Assess(game, unit.Nation, controller.Id)
            .First(province => string.Equals(
                province.Territory.Id,
                destinationId,
                StringComparison.OrdinalIgnoreCase));
        bool moverAlreadyDefendsDestination = string.Equals(
            unit.TerritoryId,
            destinationId,
            StringComparison.OrdinalIgnoreCase);
        int defendersAfterMove = homeDefense.FriendlyArmyDefenders + (moverAlreadyDefendsDestination ? 0 : 1);

        // Sequential maneuver releases surplus armies first. Once earlier moves leave only enough units
        // to match reachable attackers, the last needed defender becomes much more likely to remain.
        return homeDefense.LandThreat > 0 && defendersAfterMove <= homeDefense.LandThreat
            ? score + NeededHomeDefenderScoreBonus
            : score;
    }

    private static double ScoreTaxation(Game game, NationState nationState)
    {
        var preview = TaxationHelper.PreviewTaxation(game, nationState);
        int actualPowerGain = Math.Min(
            preview.ExpectedPowerGain,
            Math.Max(0, GameConstants.MaxPowerPoints - nationState.Power));

        // Score what Taxation will actually deliver. Power is the lasting victory-value gain, while
        // treasury and the controller's cash bonus are both immediately spendable resources.
        double score = actualPowerGain * TaxPowerGainScoreWeight
            + preview.ExpectedTreasuryGain * TaxTreasuryGainScoreWeight
            + preview.ExpectedBonus * TaxBonusScoreWeight;

        // A taxation result at this ceiling is strategically dominant even when the homeland needs
        // units. Softmax still leaves every positive candidate possible, but makes this choice overwhelming.
        return actualPowerGain >= 10
            && preview.ExpectedTreasuryGain >= 7
            && preview.ExpectedBonus >= 5
                ? ExceptionalTaxationScore
                : score;
    }

    private double ScoreProduction(Game game, NationState nationState, Player controller)
    {
        int yield = EstimateProductionYield(game, nationState.Nation);
        if (yield == 0) return 0;

        var defense = HomeDefenseHelper.Assess(game, nationState.Nation, controller.Id);
        int usableArmyFactories = CountUsableFactories(game, nationState.Nation, CityType.Brown);
        int usableFleetFactories = CountUsableFactories(game, nationState.Nation, CityType.LightBlue);
        int landDeficit = defense.Sum(province => province.LandDefenseDeficit);
        int navalThreat = defense.Sum(province => province.AdjacentEnemyFleets);

        return Math.Min(MaximumEmergencyRondelScore, yield * 8
            + Math.Min(landDeficit, usableArmyFactories) * ProductionLandDangerBonus
            + Math.Min(navalThreat, usableFleetFactories) * ProductionNavalDangerBonus);
    }

    private double ScoreImport(Game game, NationState nationState, Player controller)
    {
        if (nationState.Treasury <= 0 || !HasLegalImport(game, nationState.Nation)) return 0;

        var defense = HomeDefenseHelper.Assess(game, nationState.Nation, controller.Id);
        int landDeficit = defense.Sum(province => province.LandDefenseDeficit);
        int navalThreat = defense.Sum(province => province.AdjacentEnemyFleets);
        int occupiedHomes = defense.Count(province => province.IsOccupied);
        bool homelandInDanger = landDeficit > 0 || occupiedHomes > 0 || navalThreat > 0;
        if (!homelandInDanger) return BaseImportWeight;

        int usableArmyFactories = CountUsableFactories(game, nationState.Nation, CityType.Brown);
        int usableFleetFactories = CountUsableFactories(game, nationState.Nation, CityType.LightBlue);
        int usableFactories = usableArmyFactories + usableFleetFactories;
        int missingFactoryCapacity = Math.Max(0, landDeficit - usableArmyFactories)
            + Math.Max(0, navalThreat - usableFleetFactories);

        double score = BaseImportWeight
            + occupiedHomes * OccupiedHomeImportBonus
            + (usableFactories <= 2 ? LimitedProductionImportBonus : 0)
            + (landDeficit + navalThreat) * ImportDefenseDeficitBonus
            + missingFactoryCapacity * MissingFactoryCapacityImportBonus;

        // When Production capacity is the reason Import matters, keep Import clearly ahead of what those
        // factories could produce this turn. The ceiling leaves exceptional Taxation dominant.
        if (occupiedHomes > 0 || usableFactories <= 2 || missingFactoryCapacity > 0)
        {
            score = Math.Max(score, ScoreProduction(game, nationState, controller) + LimitedProductionImportBonus);
        }

        return Math.Min(MaximumEmergencyRondelScore, score);
    }

    public override List<(UnitType Type, string TerritoryId)> ChooseImports(
        Game game,
        NationState nationState,
        int maxImport,
        List<Territory> homeTerritories)
    {
        var imports = new List<(UnitType Type, string TerritoryId)>();
        var nation = nationState.Nation;
        var defense = HomeDefenseHelper.Assess(game, nation, nationState.ControllerId);
        int armies = game.Units.Count(unit => unit.Nation == nation && unit.UnitType == UnitType.Army);
        int fleets = game.Units.Count(unit => unit.Nation == nation && unit.UnitType == UnitType.Fleet);
        bool preferArmyForFactoryBalance = CountUsableFactories(game, nation, CityType.Brown)
            < CountUsableFactories(game, nation, CityType.LightBlue);
        var safeHomes = homeTerritories.Where(territory => !game.Units.Any(unit =>
            unit.UnitType == UnitType.Army
            && unit.IsHostile
            && unit.Nation != nation
            && string.Equals(unit.TerritoryId, territory.Id, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        while (imports.Count < maxImport)
        {
            var candidates = new List<(UnitType Type, string TerritoryId, double Weight)>();
            int plannedArmies = imports.Count(imported => imported.Type == UnitType.Army);
            int plannedFleets = imports.Count(imported => imported.Type == UnitType.Fleet);
            bool landDanger = defense.Any(province => province.LandDefenseDeficit > 0 || province.IsOccupied);
            bool navalDanger = defense.Any(province => province.AdjacentEnemyFleets > 0);

            foreach (var territory in safeHomes)
            {
                var province = defense.First(item => string.Equals(
                    item.Territory.Id,
                    territory.Id,
                    StringComparison.OrdinalIgnoreCase));

                bool canImportArmy = armies + plannedArmies < NationData.GetMaxArmies(nation)
                    && !string.Equals(territory.Id, LondonTerritoryId, StringComparison.OrdinalIgnoreCase);
                if (canImportArmy)
                {
                    int plannedHere = imports.Count(imported => imported.Type == UnitType.Army
                        && string.Equals(imported.TerritoryId, territory.Id, StringComparison.OrdinalIgnoreCase));
                    int remainingDirectDeficit = Math.Max(0, province.LandDefenseDeficit - plannedHere);
                    int reachableOccupations = defense.Count(occupied => occupied.IsOccupied
                        && HomeDefenseHelper.CanArmyReach(game, nation, territory.Id, occupied.Territory.Id));
                    double weight = BaseImportWeight
                        + (armies + plannedArmies < fleets + plannedFleets ? LowerUnitCountImportWeight : 0)
                        + (preferArmyForFactoryBalance ? ThreatenedUnitTypeImportWeight : 0)
                        + (landDanger ? ThreatenedUnitTypeImportWeight : 0)
                        + remainingDirectDeficit * DirectThreatImportWeight
                        + reachableOccupations * ReachOccupiedHomeImportWeight;
                    candidates.Add((UnitType.Army, territory.Id, weight));
                }

                bool canImportFleet = fleets + plannedFleets < NationData.GetMaxFleets(nation)
                    && territory.CityType == CityType.LightBlue;
                if (canImportFleet)
                {
                    int plannedHere = imports.Count(imported => imported.Type == UnitType.Fleet
                        && string.Equals(imported.TerritoryId, territory.Id, StringComparison.OrdinalIgnoreCase));
                    int remainingNavalThreat = Math.Max(0, province.AdjacentEnemyFleets - plannedHere);
                    double weight = BaseImportWeight
                        + (fleets + plannedFleets < armies + plannedArmies ? LowerUnitCountImportWeight : 0)
                        + (navalDanger ? ThreatenedUnitTypeImportWeight : 0)
                        + remainingNavalThreat * DirectThreatImportWeight;
                    candidates.Add((UnitType.Fleet, territory.Id, weight));
                }
            }

            if (candidates.Count == 0) break;
            double roll = Random.Shared.NextDouble() * candidates.Sum(candidate => candidate.Weight);
            double cumulative = 0;
            var selected = candidates[^1];
            foreach (var candidate in candidates)
            {
                cumulative += candidate.Weight;
                if (roll <= cumulative)
                {
                    selected = candidate;
                    break;
                }
            }

            imports.Add((selected.Type, selected.TerritoryId));
        }

        return imports;
    }

    private static int CountUsableFactories(Game game, Nation nation, CityType cityType)
    {
        return game.TerritoryStates.Count(state => state.HasFactory
            && TerritoryData.AllTerritories.Any(territory => territory.Id == state.TerritoryId
                && territory.Nation == nation
                && territory.CityType == cityType)
            && !game.Units.Any(unit => unit.TerritoryId == state.TerritoryId
                && unit.UnitType == UnitType.Army
                && unit.Nation != nation
                && unit.IsHostile));
    }

    private static bool HasLegalImport(Game game, Nation nation)
    {
        bool armyAvailable = game.Units.Count(unit => unit.Nation == nation && unit.UnitType == UnitType.Army)
            < NationData.GetMaxArmies(nation);
        bool fleetAvailable = game.Units.Count(unit => unit.Nation == nation && unit.UnitType == UnitType.Fleet)
            < NationData.GetMaxFleets(nation);

        return TerritoryData.AllTerritories.Any(territory => territory.Nation == nation
            && !game.Units.Any(unit => unit.TerritoryId == territory.Id
                && unit.UnitType == UnitType.Army
                && unit.Nation != nation
                && unit.IsHostile)
            && ((armyAvailable && !string.Equals(territory.Id, LondonTerritoryId, StringComparison.OrdinalIgnoreCase))
                || (fleetAvailable && territory.CityType == CityType.LightBlue)));
    }
}
