using System.Net;
using ServerMonitor.IntegrationTests.Infrastructure;

namespace ServerMonitor.IntegrationTests;

public sealed class HealthCheckTests(IntegrationFixture fixture)
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Api_reports_healthy_with_database()
    {
        using var client = fixture.Api.CreateClient();

        await AssertHealthyAsync(client, Ct);
    }

    [Fact]
    public async Task Gateway_reports_healthy_with_database()
    {
        using var client = fixture.Gateway.CreateClient();

        await AssertHealthyAsync(client, Ct);
    }

    private static async Task AssertHealthyAsync(HttpClient client, CancellationToken ct)
    {
        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(ct)).ShouldBe("Healthy");
    }
}
