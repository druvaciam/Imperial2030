using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using System.Text.Json;
using Microsoft.JSInterop;

namespace Imperial2030.Client.Services;

public class CustomAuthenticationStateProvider : AuthenticationStateProvider
{
    private ClaimsPrincipal _anonymous = new ClaimsPrincipal(new ClaimsIdentity());

    private readonly Microsoft.JSInterop.IJSRuntime _jsRuntime;
    public CustomAuthenticationStateProvider(Microsoft.JSInterop.IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime;
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        try
        {
            var token = await _jsRuntime.InvokeAsync<string>("localStorage.getItem", "authToken");

            if (string.IsNullOrWhiteSpace(token) || token == "null")
            {
                return new AuthenticationState(_anonymous);
            }

            var claims = ParseClaimsFromJwt(token).ToList();
            
            // Check for expiration
            var expClaim = claims.FirstOrDefault(c => c.Type == "exp");
            if (expClaim != null && long.TryParse(expClaim.Value, out var exp))
            {
                if (DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime <= DateTime.UtcNow)
                {
                    await _jsRuntime.InvokeVoidAsync("localStorage.removeItem", "authToken");
                    return new AuthenticationState(_anonymous);
                }
            }

            if (!claims.Any())
            {
                return new AuthenticationState(_anonymous);
            }

            var identity = new ClaimsIdentity(claims, "jwt");
            var user = new ClaimsPrincipal(identity);
            return new AuthenticationState(user);
        }
        catch
        {
             // JSInterop may fail (e.g. during pre-render or if not ready), fallback to anon
            return new AuthenticationState(_anonymous);
        }
    }

    public async Task MarkUserAsAuthenticated(string token)
    {
        try
        {
            await _jsRuntime.InvokeVoidAsync("localStorage.setItem", "authToken", token);
            var identity = new ClaimsIdentity(ParseClaimsFromJwt(token), "jwt");
            var user = new ClaimsPrincipal(identity);
            var authState = Task.FromResult(new AuthenticationState(user));

            NotifyAuthenticationStateChanged(authState);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error marking user as authenticated: {ex.Message}");
        }
    }

    public async Task MarkUserAsLoggedOut()
    {
        try
        {
            await _jsRuntime.InvokeVoidAsync("localStorage.removeItem", "authToken");
            var authState = Task.FromResult(new AuthenticationState(_anonymous));
            NotifyAuthenticationStateChanged(authState);
        }
        catch (Exception ex)
        {
             Console.WriteLine($"Error marking user as logged out: {ex.Message}");
        }
    }

    private IEnumerable<Claim> ParseClaimsFromJwt(string jwt)
    {
        try 
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return Enumerable.Empty<Claim>();

            var payload = parts[1];
            var jsonBytes = ParseBase64WithoutPadding(payload);
            var keyValuePairs = JsonSerializer.Deserialize<Dictionary<string, object>>(jsonBytes);

            return keyValuePairs?.Select(kvp => new Claim(kvp.Key, kvp.Value?.ToString() ?? "")) ?? Enumerable.Empty<Claim>();
        }
        catch
        {
            return Enumerable.Empty<Claim>();
        }
    }

    private byte[] ParseBase64WithoutPadding(string base64)
    {
        switch (base64.Length % 4)
        {
            case 2: base64 += "=="; break;
            case 3: base64 += "="; break;
        }
        return Convert.FromBase64String(base64);
    }
}
