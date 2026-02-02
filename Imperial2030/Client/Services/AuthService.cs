using Imperial2030.Shared.Auth;
using System.Net.Http.Json;

namespace Imperial2030.Client.Services;

public class AuthService
{
    private readonly HttpClient _httpClient;
    private readonly CustomAuthenticationStateProvider _authStateProvider;

    public AuthService(HttpClient httpClient, CustomAuthenticationStateProvider authStateProvider)
    {
        _httpClient = httpClient;
        _authStateProvider = authStateProvider;
    }

    public async Task<LoginResult> Login(LoginRequest loginRequest)
    {
        var response = await _httpClient.PostAsJsonAsync("api/auth/login", loginRequest);
        var result = await response.Content.ReadFromJsonAsync<LoginResult>();

        if (result != null && result.Successful)
        {
            await _authStateProvider.MarkUserAsAuthenticated(result.Token!);
            _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", result.Token);
        }

        return result ?? new LoginResult { Successful = false, Error = "Failed to parse response." };
    }

    public async Task<LoginResult> Register(RegisterRequest registerRequest)
    {
        var response = await _httpClient.PostAsJsonAsync("api/auth/register", registerRequest);
        return await response.Content.ReadFromJsonAsync<LoginResult>() ?? new LoginResult { Successful = false, Error = "Failed to parse response." };
    }

    public async Task Logout()
    {
        await _authStateProvider.MarkUserAsLoggedOut();
        _httpClient.DefaultRequestHeaders.Authorization = null;
    }
}
