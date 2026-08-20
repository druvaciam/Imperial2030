using Imperial2030.Shared.Auth;
using Microsoft.Extensions.Localization;
using System.Net.Http.Json;

namespace Imperial2030.Client.Services;

public class AuthService
{
    private readonly HttpClient _httpClient;
    private readonly CustomAuthenticationStateProvider _authStateProvider;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public AuthService(HttpClient httpClient, CustomAuthenticationStateProvider authStateProvider,
        IStringLocalizer<SharedResource> localizer)
    {
        _httpClient = httpClient;
        _authStateProvider = authStateProvider;
        _localizer = localizer;
    }

    /// <summary>
    /// Shown when the server's response body could not be deserialized into a LoginResult, so there
    /// is no server-supplied error text to display instead.
    /// </summary>
    private string ParseFailureMessage => _localizer["Auth_ParseResponseFailed"];

    public async Task<LoginResult> Login(LoginRequest loginRequest)
    {
        var response = await _httpClient.PostAsJsonAsync("api/auth/login", loginRequest);
        var result = await response.Content.ReadFromJsonAsync<LoginResult>();

        if (result != null && result.Successful)
        {
            await _authStateProvider.MarkUserAsAuthenticated(result.Token!);
        }

        return result ?? new LoginResult { Successful = false, Error = ParseFailureMessage };
    }

    public async Task<LoginResult> Register(RegisterRequest registerRequest)
    {
        var response = await _httpClient.PostAsJsonAsync("api/auth/register", registerRequest);
        return await response.Content.ReadFromJsonAsync<LoginResult>() ?? new LoginResult { Successful = false, Error = ParseFailureMessage };
    }

    public async Task<LoginResult> LoginAsGuest()
    {
        var response = await _httpClient.PostAsync("api/auth/guest-login", null);
        var result = await response.Content.ReadFromJsonAsync<LoginResult>();

        if (result != null && result.Successful)
        {
            await _authStateProvider.MarkUserAsAuthenticated(result.Token!);
        }

        return result ?? new LoginResult { Successful = false, Error = ParseFailureMessage };
    }

    public async Task Logout()
    {
        await _authStateProvider.MarkUserAsLoggedOut();
    }
}
