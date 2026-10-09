namespace ServerMonitor.Contracts.Users;

public sealed record UserDto(Guid Id, string Email, string Role);

public sealed record CreateUserRequest(string Email, string Password, string Role);
