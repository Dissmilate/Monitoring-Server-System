using System.Net;
using System.Net.Http.Json;
using ServerMonitor.Contracts.Auth;
using ServerMonitor.IntegrationTests.Infrastructure;

namespace ServerMonitor.IntegrationTests;

public sealed class AuthTests(IntegrationFixture fixture)
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Profile_requires_authentication()
    {
        using var client = fixture.CreateClient();

        using var response = await client.GetAsync(new Uri("/api/auth/me", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // Separate user: failed logins change AccessFailedCount and would race with other tests.
    [Fact]
    public async Task Wrong_password_is_rejected()
    {
        using var admin = await fixture.CreateAdminClientAsync(Ct);
        var (email, _) = await admin.CreateUserAsync(Roles.Viewer, Ct);
        using var client = fixture.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "wrong-password"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.Contains("Set-Cookie").ShouldBeFalse();
    }

    [Fact]
    public async Task Repeated_failures_lock_the_account_even_for_the_right_password()
    {
        using var admin = await fixture.CreateAdminClientAsync(Ct);
        var (email, password) = await admin.CreateUserAsync(Roles.Viewer, Ct);
        using var client = fixture.CreateClient();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            (await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "wrong-password"), Ct)).Dispose();
        }

        using var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("locked");
    }

    [Fact]
    public async Task Admin_logs_in_with_an_http_only_strict_cookie_and_reads_profile()
    {
        using var client = fixture.CreateClient();

        using var login = await client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest(IntegrationFixture.AdminEmail, IntegrationFixture.AdminPassword), Ct);

        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        var cookie = login.Headers.GetValues("Set-Cookie").ShouldHaveSingleItem();
        cookie.ShouldContain("sm.auth=");
        cookie.ShouldContain("httponly", Case.Insensitive);
        cookie.ShouldContain("samesite=strict", Case.Insensitive);

        var me = await client.GetFromJsonAsync<CurrentUserDto>("/api/auth/me", Ct);
        me!.Email.ShouldBe(IntegrationFixture.AdminEmail);
        me.Roles.ShouldBe([Roles.Admin]);
    }

    [Fact]
    public async Task Logout_ends_the_session()
    {
        using var client = await fixture.CreateAdminClientAsync(Ct);

        using var logout = await client.PostAsync(new Uri("/api/auth/logout", UriKind.Relative), content: null, Ct);
        logout.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var me = await client.GetAsync(new Uri("/api/auth/me", UriKind.Relative), Ct);
        me.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
