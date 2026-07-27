using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace SirkAgent.Service.Core;

internal sealed record SchedulerTrigger(
    string Name,
    DateTimeOffset TimestampUtc,
    string? Detail = null);

internal sealed class AgentScheduler
{
    private readonly string _watchDirectory;
    private readonly string _watchFileName;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _debounce;
    private readonly bool _runOnce;

    public AgentScheduler(
        string watchDirectory,
        string watchFileName,
        TimeSpan interval,
        TimeSpan debounce,
        bool runOnce)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(watchDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(watchFileName);

        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval));
        if (debounce < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(debounce));

        _watchDirectory = watchDirectory;
        _watchFileName = watchFileName;
        _interval = interval;
        _debounce = debounce;
        _runOnce = runOnce;
    }

    public async IAsyncEnumerable<SchedulerTrigger> RunAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new SchedulerTrigger("Startup", DateTimeOffset.UtcNow);

        if (_runOnce)
            yield break;

        var changes = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

        using var watcher = new FileSystemWatcher(_watchDirectory, _watchFileName)
        {
            NotifyFilter = NotifyFilters.FileName |
                           NotifyFilters.LastWrite |
                           NotifyFilters.Size |
                           NotifyFilters.CreationTime,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true
        };

        void Signal(string detail) => changes.Writer.TryWrite(detail);

        watcher.Changed += (_, _) => Signal("Changed");
        watcher.Created += (_, _) => Signal("Created");
        watcher.Deleted += (_, _) => Signal("Deleted");
        watcher.Renamed += (_, _) => Signal("Renamed");
        watcher.Error += (_, eventArgs) => Signal("WatcherError:" + eventArgs.GetException().GetType().Name);

        while (!cancellationToken.IsCancellationRequested)
        {
            var intervalTask = Task.Delay(_interval, cancellationToken);
            var changeTask = changes.Reader.ReadAsync(cancellationToken).AsTask();

            Task completed;
            try
            {
                completed = await Task.WhenAny(intervalTask, changeTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            if (completed == intervalTask)
            {
                yield return new SchedulerTrigger("Interval", DateTimeOffset.UtcNow);
                continue;
            }

            string detail;
            try
            {
                detail = await changeTask.ConfigureAwait(false);
                if (_debounce > TimeSpan.Zero)
                    await Task.Delay(_debounce, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            while (changes.Reader.TryRead(out var additionalDetail))
                detail = additionalDetail;

            yield return new SchedulerTrigger("FileSystemWatcher", DateTimeOffset.UtcNow, detail);
        }
    }
}
