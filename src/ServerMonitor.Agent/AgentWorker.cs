using ServerMonitor.Agent.Enrollment;
using ServerMonitor.Agent.Transport;

namespace ServerMonitor.Agent;

internal sealed partial class AgentWorker(
    EnrollmentService enrollment,
    AgentSession session,
    IHostApplicationLifetime lifetime,
    ILogger<AgentWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var credentials = await TransientRetry.ExecuteAsync(enrollment.EnsureEnrolledAsync, logger, stoppingToken);
            session.Start(credentials);
            LogReady(logger, credentials.ServerId);
        }
        catch (AgentEnrollmentException ex)
        {
            LogCannotStart(logger, ex.Message);
            Environment.ExitCode = 1;
            lifetime.StopApplication();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent is ready as server {ServerId}")]
    private static partial void LogReady(ILogger logger, Guid serverId);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Agent cannot start: {Reason}")]
    private static partial void LogCannotStart(ILogger logger, string reason);
}
