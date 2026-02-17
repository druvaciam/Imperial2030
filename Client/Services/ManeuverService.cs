using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;

namespace Imperial2030.Client.Services;

public class ManeuverService
{
    private readonly HttpClient _http;

    public ManeuverService(HttpClient http)
    {
        _http = http;
    }


    public async Task<bool> MoveFleet(Guid gameId, Guid unitId, string destinationId, Imperial2030.Shared.Models.Nation? battleTarget = null)
    {
        var request = new MoveUnitRequest 
        { 
            UnitId = unitId, 
            DestinationId = destinationId,
            BattleTargetNation = battleTarget
        };
        // var request = new { UnitId = unitId, DestinationId = destinationId }; // Replaced with typed request
        var response = await _http.PostAsJsonAsync($"api/maneuver/{gameId}/move-fleet", request);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> BattleAsync(Guid gameId, Guid unitId, Imperial2030.Shared.Models.Nation targetNation)
    {
        var request = new MoveUnitRequest 
        { 
            UnitId = unitId, 
            BattleTargetNation = targetNation 
        };
        var response = await _http.PostAsJsonAsync($"api/maneuver/{gameId}/battle", request);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> NextPhase(Guid gameId)
    {
        var response = await _http.PostAsync($"api/maneuver/{gameId}/next-phase", null);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> MoveArmy(Guid gameId, Guid unitId, string destinationId, List<Guid>? convoyFleetIds = null, Imperial2030.Shared.Models.Nation? battleTarget = null)
    {
        var request = new MoveUnitRequest 
        { 
            UnitId = unitId, 
            DestinationId = destinationId,
            ConvoyFleetIds = convoyFleetIds,
            BattleTargetNation = battleTarget
        };
        var response = await _http.PostAsJsonAsync($"api/maneuver/{gameId}/move-army", request);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> DestroyFactoryAsync(Guid gameId, string territoryId, List<Guid> unitIds)
    {
        var request = new DestroyFactoryRequest 
        { 
            TerritoryId = territoryId, 
            UnitIds = unitIds 
        };
        var response = await _http.PostAsJsonAsync($"api/maneuver/{gameId}/destroy-factory", request);
        return response.IsSuccessStatusCode;
    }
}
