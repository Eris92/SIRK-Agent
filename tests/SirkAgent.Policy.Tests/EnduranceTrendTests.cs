using SirkAgent.Service;
using Xunit;

namespace SirkAgent.Policy.Tests;

public sealed class EnduranceTrendTests
{
    [Fact]
    public void Memory_trend_does_not_cross_process_restart()
    {
        var start = DateTimeOffset.UtcNow.AddHours(-2);
        var samples = Enumerable.Range(0, 12).Select(index =>
            Sample(start.AddMinutes(index * 5), 100, 40_000_000 + index * 2_000_000L))
            .Concat(Enumerable.Range(0, 12).Select(index =>
                Sample(start.AddHours(1).AddMinutes(index * 5), 200,
                    70_000_000 + index * 100_000L))).ToArray();

        var summary = EnduranceWorker.BuildSummary(samples, TimeSpan.FromMinutes(5));

        Assert.Equal(1, summary.ProcessRestarts);
        Assert.False(summary.MemoryLeakSuspected);
        Assert.Equal(900_000, summary.WorkingSetGrowthBytes);
        Assert.Equal("Healthy", summary.Status);
    }

    [Fact]
    public void Single_startup_sample_has_zero_trend()
    {
        var summary = EnduranceWorker.BuildSummary(
            [Sample(DateTimeOffset.UtcNow, 400, 80_000_000)], TimeSpan.FromMinutes(5));

        Assert.Equal(0, summary.WorkingSetGrowthBytes);
        Assert.Equal(0, summary.WorkingSetGrowthPerHour);
        Assert.False(summary.MemoryLeakSuspected);
    }

    [Fact]
    public void Short_warmup_does_not_trigger_hourly_extrapolation_alarm()
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(-1);
        var samples = Enumerable.Range(0, 13).Select(index =>
            Sample(start.AddSeconds(index * 5), 500, 70_000_000 + index * 300_000L)).ToArray();

        var summary = EnduranceWorker.BuildSummary(samples, TimeSpan.FromSeconds(5));

        Assert.True(summary.WorkingSetGrowthPerHour > 5L * 1024 * 1024);
        Assert.False(summary.MemoryLeakSuspected);
        Assert.Equal("Healthy", summary.Status);
    }

    [Fact]
    public void Detects_growth_within_one_process()
    {
        var start = DateTimeOffset.UtcNow.AddHours(-1);
        var samples = Enumerable.Range(0, 13).Select(index =>
            Sample(start.AddMinutes(index * 5), 300, 40_000_000 + index * 1_000_000L)).ToArray();

        var summary = EnduranceWorker.BuildSummary(samples, TimeSpan.FromMinutes(5));

        Assert.True(summary.MemoryLeakSuspected);
        Assert.Equal("Warning", summary.Status);
    }

    private static EnduranceSample Sample(DateTimeOffset timestamp, int processId, long workingSet) =>
        new(timestamp, processId, workingSet, workingSet, 1_000_000, 1, true,
            "Healthy", "Operational", "device", 0, 0, 0, 0);
}
