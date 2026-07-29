using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;

namespace SirkAgent.Service;

internal static class InteractiveSessionClient
{
    private static readonly ConcurrentDictionary<(int SessionId, string Lane), SessionChannel> Channels = new();

    internal static Task<string?> SendAsync(int sessionId, string request, CancellationToken token) =>
        SendAsync(sessionId, "command", request, token);

    internal static Task<string?> SendCaptureAsync(int sessionId, string request, CancellationToken token) =>
        SendAsync(sessionId, "capture", request, token);

    internal static Task<string?> SendInputAsync(int sessionId, string request, CancellationToken token) =>
        SendAsync(sessionId, "input", request, token);

    private static Task<string?> SendAsync(int sessionId, string lane, string request, CancellationToken token) =>
        Channels.GetOrAdd((sessionId, lane), static key => new SessionChannel(key.SessionId))
            .SendAsync(request, token);

    internal static void Invalidate(int sessionId)
    {
        foreach (var item in Channels.Where(item => item.Key.SessionId == sessionId).ToArray())
            if (Channels.TryRemove(item.Key, out var channel)) channel.Close();
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
