using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using ServerMonitor.Api;
using ServerMonitor.Gateway;
using ServerMonitor.Infrastructure;

namespace ServerMonitor.IntegrationTests.Infrastructure;

public sealed class ApiFactory(IReadOnlyDictionary<string, string?> settings) : WebApplicationFactory<IApiMarker>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseTestSettings(settings);

        // Static web assets are only enabled in Development by default.
        builder.UseStaticWebAssets();

        builder.ConfigureServices((context, services) => services.AddDatabaseInitializer(context.Configuration));
    }
}

public sealed class GatewayFactory(IReadOnlyDictionary<string, string?> settings) : WebApplicationFactory<IGatewayMarker>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseTestSettings(settings);
}

internal static class TestHostSettings
{
    public static IReadOnlyDictionary<string, string?> Create(string connectionString, string kafkaBootstrapServers, string openSearchUrl) =>
        new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{DependencyInjection.ConnectionStringName}"] = connectionString,
            ["Kafka:BootstrapServers"] = kafkaBootstrapServers,
            ["OpenSearch:Url"] = openSearchUrl,
            ["Seed:AdminEmail"] = IntegrationFixture.AdminEmail,
            ["Seed:AdminPassword"] = IntegrationFixture.AdminPassword,
            ["Logging:LogLevel:Default"] = "Warning",

            ["AgentChannel:HeartbeatInterval"] = "00:00:01",
            ["AgentChannel:HelloTimeout"] = "00:00:02",
            ["Presence:OfflineAfter"] = "00:00:03",
            ["Presence:CheckInterval"] = "00:00:00.500",
            ["CommandRelay:Interval"] = "00:00:00.200",
        };

    public static void UseTestSettings(this IWebHostBuilder builder, IReadOnlyDictionary<string, string?> settings)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
    }
}
