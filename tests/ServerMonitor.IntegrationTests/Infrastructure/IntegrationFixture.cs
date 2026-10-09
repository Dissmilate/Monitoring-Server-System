using Grpc.Net.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ServerMonitor.Infrastructure.Logs;
using ServerMonitor.Infrastructure.Messaging;
using ServerMonitor.Infrastructure.Persistence;
using ServerMonitor.IntegrationTests.Infrastructure;
using ServerMonitor.Workers;
using Testcontainers.Kafka;
using Testcontainers.OpenSearch;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(IntegrationFixture))]

namespace ServerMonitor.IntegrationTests.Infrastructure;

public sealed class IntegrationFixture : IAsyncLifetime
{
    public const string PostgresImage = "timescale/timescaledb:2.30.2-pg17";
    // Kafka 4.3 fails to start under Testcontainers 4.15 (empty advertised.listeners).
    public const string KafkaImage = "apache/kafka:4.0.2";
    public const string OpenSearchImage = "opensearchproject/opensearch:3.9.0";
    public const string AdminEmail = "admin@test.local";
    public const string AdminPassword = "Admin-Password-1";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder(PostgresImage).Build();
    private readonly KafkaContainer _kafka = new KafkaBuilder(KafkaImage).Build();

    private readonly OpenSearchContainer _openSearch = new OpenSearchBuilder(OpenSearchImage)
        .WithSecurityEnabled(false)
        .WithEnvironment("OPENSEARCH_JAVA_OPTS", "-Xms512m -Xmx512m")
        .Build();

    private IHost? _workers;

    public ApiFactory Api { get; private set; } = null!;

    public GatewayFactory Gateway { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _kafka.StartAsync(), _openSearch.StartAsync());
        var settings = TestHostSettings.Create(
            _postgres.GetConnectionString(), _kafka.GetBootstrapAddress(), _openSearch.GetConnectionString());

        await new KafkaTopicProvisioner(
                Options.Create(new KafkaOptions { BootstrapServers = _kafka.GetBootstrapAddress() }),
                NullLogger<KafkaTopicProvisioner>.Instance)
            .EnsureTopicsAsync(CancellationToken.None);

        Api = new ApiFactory(settings);
        Gateway = new GatewayFactory(settings);

        await using (var scope = Api.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync(CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<OpenSearchProvisioner>().EnsureAsync(CancellationToken.None);
        }

        _workers = BuildWorkers(settings);
        await _workers.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_workers is not null)
        {
            await _workers.StopAsync();
            _workers.Dispose();
        }

        if (Gateway is not null)
        {
            await Gateway.DisposeAsync();
        }

        if (Api is not null)
        {
            await Api.DisposeAsync();
        }

        await _openSearch.DisposeAsync();
        await _kafka.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    public HttpClient CreateClient() => Api.CreateClient();

    public async Task<HttpClient> CreateAdminClientAsync(CancellationToken ct)
    {
        var client = CreateClient();
        await client.LoginAsync(AdminEmail, AdminPassword, ct);
        return client;
    }

    public GrpcChannel CreateGatewayChannel() =>
        GrpcChannel.ForAddress(
            Gateway.Server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = Gateway.Server.CreateHandler() });

    private static IHost BuildWorkers(IReadOnlyDictionary<string, string?> settings)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = "Testing",
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Configuration.AddInMemoryCollection(settings);
        builder.AddWorkerServices();
        return builder.Build();
    }
}
