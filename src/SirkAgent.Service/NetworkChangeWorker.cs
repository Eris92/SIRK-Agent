using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Hosting;

namespace SirkAgent.Service;

internal sealed class PortalReconnectSignal
{
    private long _generation;
    private TaskCompletionSource<long> _changed = NewSource();
    public long Generation => Interlocked.Read(ref _generation);

    public void Signal()
    {
        var generation = Interlocked.Increment(ref _generation);
        var replacement = NewSource();
        var previous = Interlocked.Exchange(ref _changed, replacement);
        previous.TrySetResult(generation);
    }

    public async Task WaitForChangeAsync(long observedGeneration, CancellationToken token)
    {
        var changed = Volatile.Read(ref _changed);
        if (Generation != observedGeneration) return;
        await changed.Task.WaitAsync(token);
    }

    private static TaskCompletionSource<long> NewSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class NetworkChangeWorker : BackgroundService
{
    private readonly PortalReconnectSignal _signal;
    private int _pending;

    public NetworkChangeWorker(PortalReconnectSignal signal) => _signal = signal;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
        try
        {
            WriteSnapshot("Startup");
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
        }
    }

    private void OnNetworkChanged(object? sender, EventArgs args) => Schedule("AddressChanged");
    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs args) =>
        Schedule(args.IsAvailable ? "NetworkAvailable" : "NetworkUnavailable");

    private void Schedule(string reason)
    {
        if (Interlocked.Exchange(ref _pending, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(250);
            try
            {
                WriteSnapshot(reason);
                _signal.Signal();
            }
            finally { Interlocked.Exchange(ref _pending, 0); }
        });
    }

    private static void WriteSnapshot(string reason)
    {
        var root = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData), "SIRK", "Agent");
        Directory.CreateDirectory(root);
        var interfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(value => value.OperationalStatus == OperationalStatus.Up)
            .Select(value => new
            {
                value.Id, value.Name, type = value.NetworkInterfaceType.ToString(),
                addresses = value.GetIPProperties().UnicastAddresses
                    .Where(address => address.Address.AddressFamily is
                        AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                    .Select(address => address.Address.ToString()).ToArray()
            }).ToArray();
        var path = Path.Combine(root, "network-status.json");
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new
        {
            timestampUtc = DateTimeOffset.UtcNow, reason,
            available = NetworkInterface.GetIsNetworkAvailable(), interfaces
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        File.Move(temporary, path, true);
    }
}
