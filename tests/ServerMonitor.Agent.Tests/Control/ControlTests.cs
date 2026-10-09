using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.Agent.Control;
using ServerMonitor.Agent.Metrics;
using ServerMonitor.Agent.Telemetry;
using ServerMonitor.Protocol.V1;

namespace ServerMonitor.Agent.Tests.Control;

public sealed class RuntimeConfigurationTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    [Fact]
    public void Defaults_apply_until_the_server_pushes_a_configuration()
    {
        var configuration = Create();

        configuration.Current.ShouldBe(new RuntimeConfig(0, TimeSpan.FromSeconds(10), RuntimeConfiguration.DefaultHeartbeatInterval));
    }

    [Fact]
    public void Applied_configuration_is_announced_and_survives_a_restart()
    {
        var configuration = Create();
        RuntimeConfig? announced = null;
        configuration.Changed += c => announced = c;

        configuration.Apply(new AgentConfig { Version = 3, MetricsIntervalSeconds = 30, HeartbeatIntervalSeconds = 5 }).ShouldBeNull();

        var expected = new RuntimeConfig(3, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5));
        announced.ShouldBe(expected);
        Create().Current.ShouldBe(expected);
    }

    [Fact]
    public void Re_applying_the_same_configuration_is_a_no_op()
    {
        var configuration = Create();
        var config = new AgentConfig { Version = 2, MetricsIntervalSeconds = 20, HeartbeatIntervalSeconds = 15 };
        configuration.Apply(config);
        var changes = 0;
        configuration.Changed += _ => changes++;

        configuration.Apply(config).ShouldBeNull();

        changes.ShouldBe(0);
    }

    [Theory]
    [InlineData(0, 15)]
    [InlineData(10, 0)]
    [InlineData(4000, 15)]
    public void Out_of_range_values_are_rejected(int metricsSeconds, int heartbeatSeconds)
    {
        var configuration = Create();

        configuration.Apply(new AgentConfig { Version = 9, MetricsIntervalSeconds = metricsSeconds, HeartbeatIntervalSeconds = heartbeatSeconds })
            .ShouldNotBeNull();

        configuration.Current.Version.ShouldBe(0);
    }

    [Fact]
    public void A_corrupt_file_falls_back_to_defaults()
    {
        _directory.Write("runtime-config.json", "{ not json");

        Create().Current.Version.ShouldBe(0);
    }

    public void Dispose() => _directory.Dispose();

    private RuntimeConfiguration Create() =>
        new(_directory.Path, TimeSpan.FromSeconds(10), NullLogger<RuntimeConfiguration>.Instance);
}

public sealed class ServerMessageHandlerTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly SqliteTelemetryBuffer _buffer;
    private readonly RuntimeConfiguration _configuration;
    private readonly MetricsSampler _sampler;
    private readonly ServerMessageHandler _handler;

    public ServerMessageHandlerTests()
    {
        _buffer = new SqliteTelemetryBuffer(_directory.Path, 100, NullLogger<SqliteTelemetryBuffer>.Instance);
        _configuration = new RuntimeConfiguration(_directory.Path, TimeSpan.FromSeconds(10), NullLogger<RuntimeConfiguration>.Instance);
        _sampler = new MetricsSampler(new StubSource(), _buffer);
        _handler = new ServerMessageHandler(_configuration, _sampler);
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_config_command_is_applied_and_acknowledged()
    {
        var message = new ServerMessage
        {
            CommandId = "cmd-1",
            Config = new AgentConfig { Version = 4, MetricsIntervalSeconds = 60, HeartbeatIntervalSeconds = 15 },
        };

        var result = await _handler.HandleAsync(message, Ct);

        result.ShouldBe(new CommandResult { CommandId = "cmd-1", Succeeded = true, Message = "Configuration v4 applied." });
        _configuration.Current.MetricsInterval.ShouldBe(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task A_config_pushed_on_connect_is_applied_without_a_result()
    {
        var message = new ServerMessage { Config = new AgentConfig { Version = 1, MetricsIntervalSeconds = 10, HeartbeatIntervalSeconds = 15 } };

        (await _handler.HandleAsync(message, Ct)).ShouldBeNull();
        _configuration.Current.Version.ShouldBe(1);
    }

    [Fact]
    public async Task An_invalid_config_is_reported_as_failed()
    {
        var message = new ServerMessage { CommandId = "cmd-2", Config = new AgentConfig { Version = 5, MetricsIntervalSeconds = 0, HeartbeatIntervalSeconds = 15 } };

        var result = await _handler.HandleAsync(message, Ct);

        result!.Succeeded.ShouldBeFalse();
        result.Message.ShouldContain("Metrics interval");
    }

    [Fact]
    public async Task Collect_now_takes_a_sample_into_the_buffer()
    {
        var result = await _handler.HandleAsync(new ServerMessage { CommandId = "cmd-3", CollectNow = new CollectNow() }, Ct);

        result!.Succeeded.ShouldBeTrue();
        (await _buffer.PeekAsync(10, Ct)).Single().Sample.CpuPercent.ShouldBe(42);
    }

    public void Dispose()
    {
        _sampler.Dispose();
        _buffer.Dispose();
        _directory.Dispose();
    }

    private sealed class StubSource : IMetricsSource
    {
        public string Description => "stub";

        public MetricSample Collect() => new() { Time = Timestamp.FromDateTime(DateTime.UtcNow), CpuPercent = 42 };
    }
}
