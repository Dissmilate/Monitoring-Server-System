using ServerMonitor.Application;
using ServerMonitor.Gateway.Agents;
using ServerMonitor.Gateway.Auth;
using ServerMonitor.Gateway.Services;
using ServerMonitor.Infrastructure;
using ServerMonitor.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplication();
builder.Services.AddInfrastructure();
builder.Services.AddKafka(builder.Configuration);
builder.Services.AddAgentAuthentication();

builder.Services.Configure<AgentChannelOptions>(builder.Configuration.GetSection(AgentChannelOptions.SectionName));
builder.Services.AddSingleton<AgentConnectionRegistry>();
builder.Services.AddSingleton<AgentControlSession>();
builder.Services.AddHostedService<CommandSignalListener>();

builder.Services.AddGrpc();
builder.Services.AddHealthChecks().AddDbContextCheck<MonitorDbContext>("database");

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapGrpcService<AgentGatewayService>();
app.MapHealthChecks("/health");

app.Run();
