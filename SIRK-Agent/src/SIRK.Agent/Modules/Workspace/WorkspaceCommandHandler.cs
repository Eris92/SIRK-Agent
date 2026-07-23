using Sirk.Agent.Commands;
using Sirk.Agent.Protocol;

namespace Sirk.Agent.Modules.Workspace;

internal sealed class WorkspaceCommandHandler : ICommandHandler
{
    private static readonly string[] SupportedMessages =
    {
        "Workspace.GetCapabilities"
    };

    public IReadOnlyCollection<string> MessageTypes => SupportedMessages;

    public ProtocolResponse Handle(ProtocolEnvelope command)
    {
        return command.MessageType switch
        {
            "Workspace.GetCapabilities" => Success(command, new
            {
                module = "Workspace",
                status = "foundation",
                capabilities = SupportedMessages,
                capture = new
                {
                    available = false,
                    reason = "Workspace.CaptureFrame has not been migrated to SIRK-Agent yet."
                }
            }),
            _ => Failure(command, "unsupported_message", "The requested Workspace messageType is not enabled.")
        };
    }

    private static ProtocolResponse Success(ProtocolEnvelope command, object result) =>
        new(1, command.RequestId, true, result, null);

    private static ProtocolResponse Failure(ProtocolEnvelope command, string code, string message) =>
        new(1, command.RequestId, false, null, new ProtocolError(code, message));
}
