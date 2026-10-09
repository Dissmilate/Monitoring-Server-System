using System.Net.Http.Json;
using System.Text.Json;
using ServerMonitor.Contracts.Auth;
using ServerMonitor.Contracts.Users;

namespace ServerMonitor.IntegrationTests.Infrastructure;

internal static class HttpClientExtensions
{
    public static async Task LoginAsync(this HttpClient client, string email, string password, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password), ct);
        response.EnsureSuccessStatusCode();
    }

    public static async Task<(string Email, string Password)> CreateUserAsync(
        this HttpClient admin, string role, CancellationToken ct)
    {
        var email = $"{UniqueName("user")}@test.local";
        const string password = "User-Password-123";

        using var response = await admin.PostAsJsonAsync("/api/users", new CreateUserRequest(email, password, role), ct);
        response.EnsureSuccessStatusCode();
        return (email, password);
    }

    public static async Task<string?> ReadProblemCodeAsync(this HttpResponseMessage response, CancellationToken ct)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return json.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    public static string UniqueName(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 13)];
}
