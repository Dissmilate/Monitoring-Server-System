using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Scalar.AspNetCore;
using ServerMonitor.Api.Auth;
using ServerMonitor.Api.Endpoints;
using ServerMonitor.Api.Live;
using ServerMonitor.Application;
using ServerMonitor.Application.Servers;
using ServerMonitor.Contracts.Live;
using ServerMonitor.Infrastructure;
using ServerMonitor.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplication();
builder.Services.Configure<EnrollmentOptions>(builder.Configuration.GetSection(EnrollmentOptions.SectionName));
builder.Services.AddInfrastructure();
builder.Services.AddKafka(builder.Configuration);
builder.Services.AddOpenSearch(builder.Configuration);
builder.Services.AddIdentityStores().AddSignInManager();
builder.Services.AddDashboardAuth();

builder.Services.AddDataProtection()
    .SetApplicationName("ServerMonitor")
    .PersistKeysToDbContext<MonitorDbContext>();

builder.Services.AddSignalR();
builder.Services.AddHostedService<LiveFeedService>();

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks().AddDbContextCheck<MonitorDbContext>("database");

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("OpenApi:Enabled"))
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}

// MapStaticAssets also serves _framework; do not add UseBlazorFrameworkFiles.
app.MapStaticAssets();

app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health");

var api = app.MapGroup("/api");
api.MapAuthEndpoints();
api.MapServerEndpoints();
api.MapMetricEndpoints();
api.MapLogEndpoints();
api.MapAgentEndpoints();
api.MapUserEndpoints();

app.MapHub<LiveUpdatesHub>(LiveHub.Path);

// Unknown /api and /hubs paths must stay 404 instead of returning index.html.
app.MapFallback("/api/{**path}", () => TypedResults.Problem(title: "Not found.", statusCode: StatusCodes.Status404NotFound));
app.MapFallback("/hubs/{**path}", () => TypedResults.NotFound());
app.MapFallbackToFile("index.html");

app.Run();
