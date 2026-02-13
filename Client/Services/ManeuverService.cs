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

    public async Task<bool> MoveFleet(Guid gameId, Guid unitId, string destinationId)
    {
        var request = new { UnitId = unitId, DestinationId = destinationId };
        var response = await _http.PostAsJsonAsync($"api/maneuver/{gameId}/move-fleet", request);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> NextPhase(Guid gameId)
    {
        var response = await _http.PostAsync($"api/maneuver/{gameId}/next-phase", null);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> MoveArmy(Guid gameId, Guid unitId, string destinationId)
    {
        var request = new { UnitId = unitId, DestinationId = destinationId };
        var response = await _http.PostAsJsonAsync($"api/maneuver/{gameId}/move-army", request);
        return response.IsSuccessStatusCode;
    }
}
