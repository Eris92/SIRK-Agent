using Sirk.Agent.Commands;
using Sirk.Agent.Protocol;

namespace Sirk.Agent.Modules.Workspace;

internal sealed class WorkspaceCommandHandler : ICommandHandler
{
    private static readonly string[] SupportedMessages =
    {
        "Workspace.GetCapabilities",
        "Workspace.CaptureFrame"
    };

    public IReadOnlyCollection<string> MessageTypes => SupportedMessages;

    public ProtocolResponse Handle(ProtocolEnvelope command)
    {
        return command.MessageType switch
        {
            "Workspace.GetCapabilities" => GetCapabilities(command),
            "Workspace.CaptureFrame" => CaptureFrame(command),
            _ => Failure(command, "unsupported_message", "The requested Workspace messageType is not enabled.")
        };
    }

    private static ProtocolResponse GetCapabilities(ProtocolEnvelope command) =>
        Success(command, new
        {
            module = "Workspace",
            status = "foundation",
            capabilities = SupportedMessages,
            capture = new
            {
                available = false,
                requestValidationAvailable = true,
                executionProvider = "not_configured",
                formats = new[] { "jpeg" },
                quality = new
                {
                    minimum = CaptureFrameRequest.MinimumQuality,
                    maximum = CaptureFrameRequest.MaximumQuality,
                    defaultValue = 70
                }
            }
        });

    private static ProtocolResponse CaptureFrame(ProtocolEnvelope command)
    {
        if (!CaptureFrameRequest.TryParse(command.Payload, out CaptureFrameRequest? request, out string error))
        {
            return Failure(command, "invalid_payload", error);
        }

        return Failure(
            command,
            "capture_provider_unavailable",
            $"Workspace capture provider is not installed for Windows session {request!.SessionId}.");
    }

    private static ProtocolResponse Success(ProtocolEnvelope command, object result) =>
        new(1, command.RequestId, true, result, null);

    private static ProtocolResponse Failure(ProtocolEnvelope command, string code, string message) =>
        new(1, command.RequestId, false, null, new ProtocolError(code, message));
}
