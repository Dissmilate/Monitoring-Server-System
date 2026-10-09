using ServerMonitor.Agent.Telemetry;
using ServerMonitor.Protocol.V1;

namespace ServerMonitor.Agent.Control;

internal sealed class ServerMessageHandler(RuntimeConfiguration configuration, MetricsSampler sampler)
{
    public async Task<CommandResult?> HandleAsync(ServerMessage message, CancellationToken ct)
    {
        var (succeeded, details) = message.PayloadCase switch
        {
            ServerMessage.PayloadOneofCase.Config => ApplyConfig(message.Config),
            ServerMessage.PayloadOneofCase.CollectNow => await CollectNowAsync(ct),
            _ => (false, $"Unsupported message: {message.PayloadCase}."),
        };

        return string.IsNullOrEmpty(message.CommandId)
            ? null
            : new CommandResult { CommandId = message.CommandId, Succeeded = succeeded, Message = details };
    }

    private (bool, string) ApplyConfig(AgentConfig config) =>
        configuration.Apply(config) is { } error
            ? (false, error)
            : (true, $"Configuration v{config.Version} applied.");

    private async Task<(bool, string)> CollectNowAsync(CancellationToken ct)
    {
        try
        {
            await sampler.SampleAsync(ct);
            return (true, "Sample collected.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, ex.Message);
        }
    }
}
