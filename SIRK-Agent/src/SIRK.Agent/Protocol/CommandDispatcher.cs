namespace Sirk.Agent.Protocol;

internal sealed class CommandDispatcher
{
    private static readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;

    public ProtocolResponse Dispatch(ProtocolEnvelope command)
    {
        return command.MessageType switch
        {
            "System.Ping" => Success(command, new
            {
                message = "pong",
                agent = "SIRK-Agent",
                protocolVersion = 1,
                utc = DateTimeOffset.UtcNow
            }),
            "System.GetStatus" => Success(command, new
            {
                status = "running",
                startedAt = StartedAt,
                uptimeSeconds = (long)(DateTimeOffset.UtcNow - StartedAt).TotalSeconds,
                processId = Environment.ProcessId,
                machineName = Environment.MachineName
            }),
            "System.GetCapabilities" => Success(command, new
            {
                capabilities = new[]
                {
                    "System.Ping",
                    "System.GetStatus",
                    "System.GetCapabilities"
                }
            }),
            _ => Failure(command, "unsupported_message", "The requested messageType is not enabled.")
        };
    }

    private static ProtocolResponse Success(ProtocolEnvelope command, object result) =>
        new(1, command.RequestId, true, result, null);

    private static ProtocolResponse Failure(ProtocolEnvelope command, string code, string message) =>
        new(1, command.RequestId, false, null, new ProtocolError(code, message));
}
