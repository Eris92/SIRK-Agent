using Sirk.Agent.Commands;
using Sirk.Agent.Protocol;

namespace Sirk.Agent.Modules.Workspace;

internal sealed class WorkspaceCommandHandler(
    IWorkspaceCaptureProvider captureProvider,
    IWindowsSessionProvider sessionProvider) : ICommandHandler
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

    private ProtocolResponse GetCapabilities(ProtocolEnvelope command)
    {
        IReadOnlyList<WindowsSessionInfo> sessions = sessionProvider.GetSessions();

        return Success(command, new
        {
            module = "Workspace",
            status = "foundation",
            capabilities = SupportedMessages,
            session = new
            {
                activeConsoleSessionId = sessionProvider.ActiveConsoleSessionId,
                sessionZeroIsolation = true,
                rdsEnumerationAvailable = true,
                sessions = sessions.Select(session => new
                {
                    sessionId = session.SessionId,
                    stationName = session.StationName,
                    state = session.State,
                    interactive = session.IsInteractive
                })
            },
            capture = new
            {
                available = captureProvider.IsAvailable,
                requestValidationAvailable = true,
                executionProvider = captureProvider.ProviderName,
                formats = new[] { "jpeg" },
                quality = new
                {
                    minimum = CaptureFrameRequest.MinimumQuality,
                    maximum = CaptureFrameRequest.MaximumQuality,
                    defaultValue = 70
                }
            }
        });
    }

    private ProtocolResponse CaptureFrame(ProtocolEnvelope command)
    {
        if (!CaptureFrameRequest.TryParse(command.Payload, out CaptureFrameRequest? request, out string error))
        {
            return Failure(command, "invalid_payload", error);
        }

        if (request!.SessionId == 0)
        {
            return Failure(
                command,
                "session_zero_blocked",
                "Workspace capture cannot execute in Windows Session 0.");
        }

        if (!sessionProvider.IsInteractiveSessionAvailable((uint)request.SessionId))
        {
            return Failure(
                command,
                "interactive_session_unavailable",
                $"Windows session {request.SessionId} is not an active interactive session.");
        }

        WorkspaceCaptureResult result = captureProvider.Capture(request);
        if (!result.Success)
        {
            return Failure(
                command,
                result.ErrorCode ?? "capture_failed",
                result.ErrorMessage ?? "Workspace capture failed safely.");
        }

        return Success(command, new
        {
            contentType = result.ContentType,
            frameBase64 = Convert.ToBase64String(result.FrameBytes ?? Array.Empty<byte>())
        });
    }

    private static ProtocolResponse Success(ProtocolEnvelope command, object result) =>
        new(1, command.RequestId, true, result, null);

    private static ProtocolResponse Failure(ProtocolEnvelope command, string code, string message) =>
        new(1, command.RequestId, false, null, new ProtocolError(code, message));
}