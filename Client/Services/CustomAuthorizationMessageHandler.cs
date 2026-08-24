using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.JSInterop;
using System.Net.Http;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace Imperial2030.Client.Services
{
    public class CustomAuthorizationMessageHandler : DelegatingHandler
    {
        private readonly IJSInProcessRuntime _jsRuntime;
        private readonly CustomAuthenticationStateProvider _authStateProvider;
        private readonly NavigationManager _navigation;

        public CustomAuthorizationMessageHandler(IJSRuntime jsRuntime, CustomAuthenticationStateProvider authStateProvider, NavigationManager navigation)
        {
            _jsRuntime = (IJSInProcessRuntime)jsRuntime;
            _authStateProvider = authStateProvider;
            _navigation = navigation;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var token = _jsRuntime.Invoke<string>("localStorage.getItem", "authToken");
            if (!string.IsNullOrWhiteSpace(token) && token != "null")
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            
            var response = await base.SendAsync(request, cancellationToken);
            
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                // A 401 means the stored token is no longer accepted — expired, or signed with a key the
                // server has since rotated away from. The client cannot detect the latter on its own:
                // CustomAuthenticationStateProvider only checks the token's `exp` claim, never whether the
                // server still honours the signature, so a stale-key token keeps the user looking signed in
                // indefinitely.
                //
                // Clearing the token alone (what this did before) left the user on the same page with a
                // failed action and no explanation — every caller reported its own generic message, so a
                // rotated key surfaced as "Failed to create game. Please check your inputs.", blaming the
                // one thing that was not wrong. Send them somewhere they can actually recover.
                await _authStateProvider.MarkUserAsLoggedOut();

                var current = _navigation.ToBaseRelativePath(_navigation.Uri).TrimStart('/');
                if (!current.StartsWith("login", System.StringComparison.OrdinalIgnoreCase))
                {
                    _navigation.NavigateTo("login", forceLoad: false);
                }
            }

            return response;
        }
    }
}
