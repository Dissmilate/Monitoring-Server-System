using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using ServerMonitor.Contracts.Auth;
using ServerMonitor.Dashboard.Services;

namespace ServerMonitor.Dashboard.Auth;

public sealed class CookieAuthenticationStateProvider(HttpClient http) : AuthenticationStateProvider
{
    private static readonly AuthenticationState Anonymous = new(new ClaimsPrincipal(new ClaimsIdentity()));

    private Task<AuthenticationState>? _state;

    public CurrentUserDto? User { get; private set; }

    public override Task<AuthenticationState> GetAuthenticationStateAsync() => _state ??= LoadAsync();

    public async Task<string?> LoginAsync(string email, string password)
    {
        using var response = await http.PostAsJsonAsync("api/auth/login", new LoginRequest(email, password));
        if (!response.IsSuccessStatusCode)
        {
            return await ApiProblem.ReadTitleAsync(response) ?? "Sign-in failed.";
        }

        SetUser(await response.Content.ReadFromJsonAsync<CurrentUserDto>());
        return null;
    }

    public async Task LogoutAsync()
    {
        using var response = await http.PostAsync("api/auth/logout", content: null);
        SetUser(null);
    }

    public void SignedOut()
    {
        if (User is not null)
        {
            SetUser(null);
        }
    }

    private async Task<AuthenticationState> LoadAsync()
    {
        using var response = await http.GetAsync("api/auth/me");
        User = response.StatusCode == HttpStatusCode.OK ? await response.Content.ReadFromJsonAsync<CurrentUserDto>() : null;
        return ToState(User);
    }

    private void SetUser(CurrentUserDto? user)
    {
        User = user;
        _state = Task.FromResult(ToState(user));
        NotifyAuthenticationStateChanged(_state);
    }

    private static AuthenticationState ToState(CurrentUserDto? user)
    {
        if (user is null)
        {
            return Anonymous;
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Email),
        };
        claims.AddRange(user.Roles.Select(role => new Claim(ClaimTypes.Role, role)));
        return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "cookie")));
    }
}

public sealed class UnauthorizedHandler(CookieAuthenticationStateProvider authentication) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            authentication.SignedOut();
        }

        return response;
    }
}
