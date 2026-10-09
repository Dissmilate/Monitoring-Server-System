using Microsoft.Extensions.Options;
using ServerMonitor.Agent;
using ServerMonitor.Agent.Control;
using ServerMonitor.Agent.Credentials;
using ServerMonitor.Agent.Enrollment;
using ServerMonitor.Agent.Logs;
using ServerMonitor.Agent.Metrics;
using ServerMonitor.Agent.Telemetry;
using ServerMonitor.Agent.Transport;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options => options.ServiceName = "ServerMonitor Agent");
builder.Services.AddSystemd();

builder.Services.AddOptions<AgentOptions>()
    .Bind(builder.Configuration.GetSection(AgentOptions.SectionName))
    .Validate(o => Uri.TryCreate(o.GatewayUrl, UriKind.Absolute, out _), "Agent:GatewayUrl must be an absolute URL.")
    .Validate(o => o.MetricsInterval >= TimeSpan.FromSeconds(1), "Agent:MetricsInterval must be at least 1 second.")
    .ValidateOnStart();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<AgentSession>();

builder.Services.AddSingleton<IGatewayClient>(sp =>
    new GrpcGatewayClient(GrpcGatewayClient.CreateChannel(sp.GetRequiredService<IOptions<AgentOptions>>().Value.GatewayUrl)));

builder.Services.AddSingleton<ICredentialStore>(sp => new FileCredentialStore(
    sp.GetRequiredService<IOptions<AgentOptions>>().Value.ResolveDataDirectory(),
    SecretProtectors.ForCurrentPlatform()));

builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
    return new RuntimeConfiguration(
        options.ResolveDataDirectory(), options.MetricsInterval, sp.GetRequiredService<ILogger<RuntimeConfiguration>>());
});

builder.Services.AddSingleton<IMetricsSource>(sp => MetricsSources.ForCurrentPlatform(
    sp.GetRequiredService<IOptions<AgentOptions>>().Value,
    sp.GetRequiredService<TimeProvider>()));

builder.Services.AddSingleton<ITelemetryBuffer>(sp =>
{
    var options = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
    return new SqliteTelemetryBuffer(
        options.ResolveDataDirectory(),
        options.BufferCapacity,
        sp.GetRequiredService<ILogger<SqliteTelemetryBuffer>>());
});

builder.Services.AddSingleton(sp => LogSources.Create(
    sp.GetRequiredService<IOptions<AgentOptions>>().Value.Logs,
    sp.GetRequiredService<TimeProvider>()));

builder.Services.AddSingleton<ILogQueue>(sp =>
{
    var options = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
    return new SqliteLogQueue(
        options.ResolveDataDirectory(),
        options.Logs.BufferCapacity,
        sp.GetRequiredService<ILogger<SqliteLogQueue>>());
});

builder.Services.AddSingleton<EnrollmentService>();
builder.Services.AddSingleton<MetricsSampler>();
builder.Services.AddSingleton<ServerMessageHandler>();

builder.Services.AddHostedService<AgentWorker>();
builder.Services.AddHostedService<MetricsCollector>();
builder.Services.AddHostedService<TelemetrySender>();
builder.Services.AddHostedService<ControlChannelService>();
builder.Services.AddHostedService<LogCollector>();
builder.Services.AddHostedService<LogSender>();

builder.Build().Run();
