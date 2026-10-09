using ApexCharts;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.Logging;
using MudBlazor.Services;
using ServerMonitor.Dashboard;
using ServerMonitor.Dashboard.Auth;
using ServerMonitor.Dashboard.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var baseAddress = new Uri(builder.HostEnvironment.BaseAddress);

builder.Services.AddSingleton(_ => new CookieAuthenticationStateProvider(new HttpClient { BaseAddress = baseAddress }));
builder.Services.AddSingleton<AuthenticationStateProvider>(sp => sp.GetRequiredService<CookieAuthenticationStateProvider>());
builder.Services.AddAuthorizationCore();

builder.Services.AddSingleton(sp => new ApiClient(new HttpClient(
    new UnauthorizedHandler(sp.GetRequiredService<CookieAuthenticationStateProvider>()) { InnerHandler = new HttpClientHandler() })
{
    BaseAddress = baseAddress,
}));

builder.Services.AddSingleton<LiveUpdates>();
builder.Services.AddMudServices();
builder.Services.AddApexCharts();

await builder.Build().RunAsync();
