using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sirk.Agent.Protocol;

namespace Sirk.Agent.Ipc;

internal sealed class NamedPipeCommandServer(
    ProtocolValidator validator,
    CommandDispatcher dispatcher,
    ILogger<NamedPipeCommandServer> logger)
{
    internal const string PipeName = "SIRK.Agent.v1";
    private const int MaximumRequestBytes = 64 * 1024;
    private const int MaximumResponseBytes = 16 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        WriteIndented = false
    };

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using NamedPipeServerStream pipe = CreatePipe();
            await pipe.WaitForConnectionAsync(cancellationToken);

            try
            {
                await ProcessConnectionAsync(pipe, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Named Pipe client failed.");
            }
        }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        PipeSecurity security = CreatePipeSecurity();
        return NamedPipeServerStreamAcl.Create(
            PipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            MaximumRequestBytes,
            MaximumRequestBytes,
            security,
            HandleInheritability.None,
            PipeAccessRights.ChangePermissions);
    }

    private static PipeSecurity CreatePipeSecurity()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddAllowRule(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        AddAllowRule(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));

        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        if (identity.User is not null)
        {
            AddAllowRule(security, identity.User);
        }

        return security;
    }

    private static void AddAllowRule(PipeSecurity security, IdentityReference identity)
    {
        const PipeAccessRights rights = PipeAccessRights.ReadData |
                                        PipeAccessRights.WriteData |
                                        PipeAccessRights.ReadAttributes |
                                        PipeAccessRights.WriteAttributes |
                                        PipeAccessRights.ReadExtendedAttributes |
                                        PipeAccessRights.WriteExtendedAttributes |
                                        PipeAccessRights.ReadPermissions |
                                        PipeAccessRights.Synchronize;
        security.AddAccessRule(new PipeAccessRule(identity, rights, AccessControlType.Allow));
    }

    private async Task ProcessConnectionAsync(Stream stream, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            byte[]? requestBytes = await ReadFrameAsync(stream, cancellationToken);
            if (requestBytes is null)
            {
                return;
            }

            ProtocolResponse response = ProcessRequest(requestBytes);
            byte[] responseBytes = JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions);
            await WriteFrameAsync(stream, responseBytes, cancellationToken);
        }
    }

    private ProtocolResponse ProcessRequest(byte[] requestBytes)
    {
        ProtocolEnvelope? command;
        try
        {
            command = JsonSerializer.Deserialize<ProtocolEnvelope>(requestBytes, JsonOptions);
        }
        catch (JsonException)
        {
            return InvalidResponse("invalid_json", "The request is not valid JSON.");
        }

        if (command is null)
        {
            return InvalidResponse("empty_request", "The request body is empty.");
        }

        ProtocolError? error = validator.Validate(command, DateTimeOffset.UtcNow);
        return error is null
            ? dispatcher.Dispatch(command)
            : new ProtocolResponse(1, command.RequestId, false, null, error);
    }

    private static ProtocolResponse InvalidResponse(string code, string message) =>
        new(1, string.Empty, false, null, new ProtocolError(code, message));

    private static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[sizeof(int)];
        int headerBytes = await ReadAtMostAsync(stream, header, cancellationToken);
        if (headerBytes == 0)
        {
            return null;
        }

        if (headerBytes != header.Length)
        {
            throw new InvalidDataException("Incomplete IPC frame header.");
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaximumRequestBytes)
        {
            throw new InvalidDataException("IPC request length is outside the allowed limit.");
        }

        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return payload;
    }

    private static async Task<int> ReadAtMostAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[total..], cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        if (payload.Length is <= 0 || payload.Length > MaximumResponseBytes)
        {
            throw new InvalidDataException("IPC response exceeds the allowed limit.");
        }

        byte[] header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}