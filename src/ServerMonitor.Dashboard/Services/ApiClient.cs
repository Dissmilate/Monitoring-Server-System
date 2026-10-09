using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using ServerMonitor.Contracts.Agents;
using ServerMonitor.Contracts.Live;
using ServerMonitor.Contracts.Logs;
using ServerMonitor.Contracts.Metrics;
using ServerMonitor.Contracts.Servers;
using ServerMonitor.Contracts.Users;

namespace ServerMonitor.Dashboard.Services;

public sealed record LogSearch(
    Guid? ServerId, IReadOnlyList<string> Levels, string? Text, DateTimeOffset From, DateTimeOffset To, int? Limit = null);

public sealed class ApiException(string message) : Exception(message);

public static class ApiProblem
{
    public static async Task<string?> ReadTitleAsync(HttpResponseMessage response)
    {
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var title = json.RootElement.TryGetProperty("title", out var t) ? t.GetString() : null;
            if (json.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                var details = errors.EnumerateObject().SelectMany(e => e.Value.EnumerateArray().Select(v => v.GetString()));
                return string.Join(" ", details.Prepend(title).Where(s => !string.IsNullOrWhiteSpace(s)));
            }

            return title;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed class ApiClient(HttpClient http)
{
    public Task<List<ServerDto>> GetServersAsync() => GetAsync<List<ServerDto>>("api/servers");

    public Task<ServerDto> GetServerAsync(Guid id) => GetAsync<ServerDto>($"api/servers/{id}");

    public Task<ServerDto> CreateServerAsync(string name) =>
        SendAsync<ServerDto>(HttpMethod.Post, "api/servers", new CreateServerRequest(name));

    public Task DeleteServerAsync(Guid id) => SendAsync(HttpMethod.Delete, $"api/servers/{id}");

    public Task<EnrollmentTokenDto> IssueEnrollmentTokenAsync(Guid id) =>
        SendAsync<EnrollmentTokenDto>(HttpMethod.Post, $"api/servers/{id}/enrollment-tokens");

    public Task RevokeAgentKeysAsync(Guid id) => SendAsync(HttpMethod.Delete, $"api/servers/{id}/agent-keys");

    public Task<MetricsDto> GetMetricsAsync(Guid id, DateTimeOffset from, DateTimeOffset to, string resolution = "auto") =>
        GetAsync<MetricsDto>(
            $"api/servers/{id}/metrics?from={Uri.EscapeDataString(from.ToString("O", CultureInfo.InvariantCulture))}" +
            $"&to={Uri.EscapeDataString(to.ToString("O", CultureInfo.InvariantCulture))}&resolution={resolution}");

    public Task<List<ServerSummaryDto>> GetLatestMetricsAsync() => GetAsync<List<ServerSummaryDto>>("api/metrics/latest");

    public Task<AgentConfigurationDto> GetConfigurationAsync(Guid id) =>
        GetAsync<AgentConfigurationDto>($"api/servers/{id}/configuration");

    public Task<AgentConfigurationDto> UpdateConfigurationAsync(Guid id, int metricsIntervalSeconds) =>
        SendAsync<AgentConfigurationDto>(
            HttpMethod.Put, $"api/servers/{id}/configuration", new UpdateAgentConfigurationRequest(metricsIntervalSeconds));

    public Task<List<AgentCommandDto>> GetCommandsAsync(Guid id) =>
        GetAsync<List<AgentCommandDto>>($"api/servers/{id}/commands?limit=10");

    public Task<AgentCommandDto> RequestCommandAsync(Guid id, string type) =>
        SendAsync<AgentCommandDto>(HttpMethod.Post, $"api/servers/{id}/commands", new RequestAgentCommandRequest(type));

    public Task<LogSearchResultDto> SearchLogsAsync(LogSearch search, string? cursor)
    {
        var query = new List<string>
        {
            "from=" + Uri.EscapeDataString(search.From.ToString("O", CultureInfo.InvariantCulture)),
            "to=" + Uri.EscapeDataString(search.To.ToString("O", CultureInfo.InvariantCulture)),
        };

        if (search.ServerId is { } serverId)
        {
            query.Add($"serverId={serverId}");
        }

        if (search.Levels.Count > 0)
        {
            query.Add("levels=" + Uri.EscapeDataString(string.Join(',', search.Levels)));
        }

        if (!string.IsNullOrWhiteSpace(search.Text))
        {
            query.Add("q=" + Uri.EscapeDataString(search.Text));
        }

        if (search.Limit is { } limit)
        {
            query.Add(string.Create(CultureInfo.InvariantCulture, $"limit={limit}"));
        }

        if (cursor is not null)
        {
            query.Add("cursor=" + Uri.EscapeDataString(cursor));
        }

        return GetAsync<LogSearchResultDto>("api/logs?" + string.Join('&', query));
    }

    public Task<List<UserDto>> GetUsersAsync() => GetAsync<List<UserDto>>("api/users");

    public Task<UserDto> CreateUserAsync(string email, string password, string role) =>
        SendAsync<UserDto>(HttpMethod.Post, "api/users", new CreateUserRequest(email, password, role));

    public Task DeleteUserAsync(Guid id) => SendAsync(HttpMethod.Delete, $"api/users/{id}");

    private async Task<T> GetAsync<T>(string url)
    {
        using var response = await http.GetAsync(url);
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string url, object? body = null)
    {
        using var response = await SendCoreAsync(method, url, body);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task SendAsync(HttpMethod method, string url, object? body = null)
    {
        using var response = await SendCoreAsync(method, url, body);
    }

    private async Task<HttpResponseMessage> SendCoreAsync(HttpMethod method, string url, object? body)
    {
        using var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        var response = await http.SendAsync(request);
        await EnsureSuccessAsync(response);
        return response;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiException(
                await ApiProblem.ReadTitleAsync(response) ?? $"Request failed ({(int)response.StatusCode}).");
        }
    }
}
