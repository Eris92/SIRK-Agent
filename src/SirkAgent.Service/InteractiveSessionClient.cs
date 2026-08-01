using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;

namespace SirkAgent.Service;

internal static class InteractiveSessionClient
{
    private static readonly ConcurrentDictionary<(int SessionId, string Lane), SessionChannel> Channels = new();
    private static readonly ConcurrentDictionary<int, BinaryCaptureChannel> BinaryChannels = new();

    internal static Task<string?> SendAsync(int sessionId, string request, CancellationToken token) =>
        SendAsync(sessionId, "command", request, token);

    internal static Task<string?> SendCaptureAsync(int sessionId, string request, CancellationToken token) =>
        SendAsync(sessionId, "capture", request, token);

    internal static Task<BinarySessionResponse> SendBinaryCaptureAsync(int sessionId, string request,
        CancellationToken token) => BinaryChannels.GetOrAdd(sessionId, static id => new BinaryCaptureChannel(id))
            .SendAsync(request, token);

    internal static Task<string?> SendInputAsync(int sessionId, string request, CancellationToken token) =>
        SendAsync(sessionId, "input", request, token);

    private static Task<string?> SendAsync(int sessionId, string lane, string request, CancellationToken token) =>
        Channels.GetOrAdd((sessionId, lane), static key => new SessionChannel(key.SessionId))
            .SendAsync(request, token);

    internal static void Invalidate(int sessionId)
    {
        foreach (var item in Channels.Where(item => item.Key.SessionId == sessionId).ToArray())
            if (Channels.TryRemove(item.Key, out var channel)) channel.Close();
        if (BinaryChannels.TryRemove(sessionId, out var binary)) binary.Close();
    }

    private sealed class BinaryCaptureChannel(int sessionId)
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private NamedPipeClientStream? _pipe;

        internal async Task<BinarySessionResponse> SendAsync(string request, CancellationToken token)
        {
            await _gate.WaitAsync(token);
            try
            {
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    try
                    {
                        await EnsureConnectedAsync(timeout.Token);
                        var requestBytes = Encoding.UTF8.GetBytes(request);
                        var size = new byte[4];
                        BinaryPrimitives.WriteInt32LittleEndian(size, requestBytes.Length);
                        await _pipe!.WriteAsync(size, timeout.Token);
                        await _pipe.WriteAsync(requestBytes, timeout.Token);
                        await _pipe.FlushAsync(timeout.Token);
                        await _pipe.ReadExactlyAsync(size, timeout.Token);
                        var headerLength = BinaryPrimitives.ReadInt32LittleEndian(size);
                        await _pipe.ReadExactlyAsync(size, timeout.Token);
                        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(size);
                        if (headerLength is < 2 or > 256 * 1024 || payloadLength is < 0 or > 16 * 1024 * 1024)
                            throw new InvalidDataException("Invalid binary desktop response.");
                        var header = new byte[headerLength];
                        var payload = new byte[payloadLength];
                        await _pipe.ReadExactlyAsync(header, timeout.Token);
                        if (payloadLength > 0) await _pipe.ReadExactlyAsync(payload, timeout.Token);
                        return new BinarySessionResponse(Encoding.UTF8.GetString(header), payload);
                    }
                    catch (IOException)
                    {
                        Reset();
                        if (attempt > 0) throw;
                    }
                    catch (OperationCanceledException) { Reset(); throw; }
                }
                throw new IOException("Binary desktop channel unavailable.");
            }
            finally { _gate.Release(); }
        }

        private async Task EnsureConnectedAsync(CancellationToken token)
        {
            if (_pipe is { IsConnected: true }) return;
            Reset();
            _pipe = new NamedPipeClientStream(".", InteractiveSessionPipe.Name(sessionId) + "-Video",
                PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
            await _pipe.ConnectAsync(token);
        }

        private void Reset() { _pipe?.Dispose(); _pipe = null; }
        internal void Close() => Reset();
    }

    private sealed class SessionChannel(int sessionId)
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private NamedPipeClientStream? _pipe;
        private StreamReader? _reader;
        private StreamWriter? _writer;

        internal async Task<string?> SendAsync(string request, CancellationToken token)
        {
            await _gate.WaitAsync(token);
            try
            {
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    try
                    {
                        await EnsureConnectedAsync(timeout.Token);
                        await _writer!.WriteLineAsync(request.AsMemory(), timeout.Token);
                        return await _reader!.ReadLineAsync(timeout.Token);
                    }
                    catch (IOException)
                    {
                        Reset();
                        if (attempt > 0) throw;
                    }
                    catch (OperationCanceledException)
                    {
                        Reset();
                        throw;
                    }
                }
                return null;
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task EnsureConnectedAsync(CancellationToken token)
        {
            if (_pipe is { IsConnected: true }) return;
            Reset();
            _pipe = new NamedPipeClientStream(".", InteractiveSessionPipe.Name(sessionId),
                PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
            await _pipe.ConnectAsync(token);
            _reader = new StreamReader(_pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
            _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
                { AutoFlush = true };
        }

        private void Reset()
        {
            _writer?.Dispose();
            _reader?.Dispose();
            _pipe?.Dispose();
            _writer = null;
            _reader = null;
            _pipe = null;
        }

        internal void Close() => Reset();
    }
}

internal sealed record BinarySessionResponse(string HeaderJson, byte[] Payload);
