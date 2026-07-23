namespace Sirk.Agent.Protocol;

internal sealed class ProtocolValidator(ReplayProtection replayProtection)
{
    private static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan AllowedClockSkew = TimeSpan.FromSeconds(30);

    public ProtocolError? Validate(ProtocolEnvelope command, DateTimeOffset now)
    {
        if (command.ProtocolVersion != 1)
        {
            return new ProtocolError("unsupported_protocol", "Only SIRK Protocol version 1 is supported.");
        }

        if (!Guid.TryParse(command.RequestId, out _))
        {
            return new ProtocolError("invalid_request_id", "requestId must be a UUID.");
        }

        if (string.IsNullOrWhiteSpace(command.MessageType) || command.MessageType.Length > 128)
        {
            return new ProtocolError("invalid_message_type", "messageType is required and limited to 128 characters.");
        }

        if (string.IsNullOrWhiteSpace(command.DeviceId) || command.DeviceId.Length > 256)
        {
            return new ProtocolError("invalid_device_id", "deviceId is required and limited to 256 characters.");
        }

        if (string.IsNullOrWhiteSpace(command.OperatorId) || command.OperatorId.Length > 256)
        {
            return new ProtocolError("invalid_operator_id", "operatorId is required and limited to 256 characters.");
        }

        if (string.IsNullOrWhiteSpace(command.Nonce) || command.Nonce.Length is < 16 or > 256)
        {
            return new ProtocolError("invalid_nonce", "nonce must contain from 16 to 256 characters.");
        }

        if (command.IssuedAt > now + AllowedClockSkew)
        {
            return new ProtocolError("issued_in_future", "The command issue time is outside the allowed clock skew.");
        }

        if (command.ExpiresAt <= now)
        {
            return new ProtocolError("expired", "The command has expired.");
        }

        if (command.ExpiresAt <= command.IssuedAt || command.ExpiresAt - command.IssuedAt > MaximumLifetime)
        {
            return new ProtocolError("invalid_lifetime", "The command lifetime is invalid or too long.");
        }

        if (!replayProtection.TryRegister(command.Nonce, command.ExpiresAt, now))
        {
            return new ProtocolError("replay_detected", "The command nonce has already been used.");
        }

        return null;
    }
}
