using ServerMonitor.Agent.Credentials;

namespace ServerMonitor.Agent;

internal sealed class AgentSession
{
    private readonly TaskCompletionSource<AgentCredentials> _credentials =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Start(AgentCredentials credentials) => _credentials.TrySetResult(credentials);

    public Task<AgentCredentials> WaitForCredentialsAsync(CancellationToken ct) => _credentials.Task.WaitAsync(ct);
}
