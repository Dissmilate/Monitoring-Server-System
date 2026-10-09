namespace ServerMonitor.Contracts.Auth;

public static class Roles
{
    public const string Admin = "Admin";
    public const string Viewer = "Viewer";

    public static readonly IReadOnlyList<string> All = [Admin, Viewer];
}

public sealed record LoginRequest(string Email, string Password);

public sealed record CurrentUserDto(Guid Id, string Email, IReadOnlyList<string> Roles);
