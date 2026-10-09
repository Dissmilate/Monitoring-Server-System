using ServerMonitor.Domain.Common;

namespace ServerMonitor.Domain.Servers;

public static class ServerErrors
{
    public static readonly Error NameRequired =
        Error.Validation("Server.NameRequired", "Server name is required.");

    public static readonly Error NameTooLong =
        Error.Validation("Server.NameTooLong", $"Server name must be at most {Server.MaxNameLength} characters.");

    public static readonly Error InvalidEnrollmentToken =
        Error.Validation("Server.InvalidEnrollmentToken", "Enrollment token is invalid, expired or already used.");

    public static readonly Error InvalidMetricsInterval = Error.Validation(
        "Server.InvalidMetricsInterval",
        $"Metrics interval must be between {Server.MinMetricsIntervalSeconds} and {Server.MaxMetricsIntervalSeconds} seconds.");

    public static readonly Error NotEnrolled =
        Error.Conflict("Server.NotEnrolled", "No agent is enrolled for this server yet.");

    public static Error NotFound(Guid id) =>
        Error.NotFound("Server.NotFound", $"Server '{id}' was not found.");

    public static Error NameTaken(string name) =>
        Error.Conflict("Server.NameTaken", $"Server name '{name}' is already in use.");
}
