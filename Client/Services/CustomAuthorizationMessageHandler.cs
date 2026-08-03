using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.JSInterop;
using System.Net.Http;
using Microsoft.AspNetCore.Components.Authorization;

namespace Imperial2030.Client.Services
{
    public class CustomAuthorizationMessageHandler : DelegatingHandler
    {
        private readonly IJSInProcessRuntime _jsRuntime;
        private readonly CustomAuthenticationStateProvider _authStateProvider;
        
        public CustomAuthorizationMessageHandler(IJSRuntime jsRuntime, CustomAuthenticationStateProvider authStateProvider)
        {
            _jsRuntime = (IJSInProcessRuntime)jsRuntime;
            _authStateProvider = authStateProvider;
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
                await _authStateProvider.MarkUserAsLoggedOut();
            }
            
            return response;
        }
    }
}
