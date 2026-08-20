using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using Imperial2030.Client;
using Imperial2030.Client.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddTransient<CustomAuthorizationMessageHandler>();
builder.Services.AddScoped(sp => 
{
    var handler = sp.GetRequiredService<CustomAuthorizationMessageHandler>();
    handler.InnerHandler = new HttpClientHandler();
    return new HttpClient(handler) { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) };
});

builder.Services.AddAuthorizationCore();
builder.Services.AddScoped<AuthenticationStateProvider, CustomAuthenticationStateProvider>();
builder.Services.AddScoped<CustomAuthenticationStateProvider>(provider => (CustomAuthenticationStateProvider)provider.GetRequiredService<AuthenticationStateProvider>());
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<ManeuverService>();
builder.Services.AddScoped<MapService>();

// ResourcesPath is required: without it IStringLocalizer<GameRoom> would look for
// Client/Pages/GameRoom.resx instead of Client/Resources/Pages/GameRoom.resx.
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");
builder.Services.AddScoped<LanguageService>();
builder.Services.AddScoped<DisplayNameLocalizer>();

// The culture must be applied before RunAsync: Blazor WebAssembly fetches satellite (.resx)
// assemblies once at host startup, for whatever culture is in effect at that point.
var host = builder.Build();
var startupCulture = await LanguageService.ResolveStartupCultureAsync(host.Services.GetRequiredService<IJSRuntime>());
LanguageService.ApplyCulture(startupCulture);

await host.RunAsync();
