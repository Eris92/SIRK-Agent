using SirkAgent.Policy;
using Xunit;

namespace SirkAgent.Policy.Tests;

public sealed class PolicyStateHealthCheckerTests
{
    [Fact]
    public void Check_InitializesMissingStateAndReturnsHealthy()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "policy-state.bin");
            var store = new RecordingPolicyStateStore(path);
            var checker = new PolicyStateHealthChecker(path, store);

            var result = checker.Check();

            Assert.True(result.IsHealthy);
            Assert.Equal(PolicyStateHealthStatus.Ok, result.Status);
            Assert.Equal("STATE_INITIALIZED", result.Code);
            Assert.Equal(1, store.SaveCount);
            Assert.Equal(PolicyState.Empty, store.SavedState);
            Assert.Equal(PolicyState.Empty, result.State);
            Assert.True(File.Exists(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Check_DoesNotReplaceExistingCorruptState()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "policy-state.bin");
            File.WriteAllBytes(path, [1, 2, 3]);
            var store = new ThrowingPolicyStateStore(new InvalidDataException("corrupt"));
            var checker = new PolicyStateHealthChecker(path, store);

            var result = checker.Check();

            Assert.False(result.IsHealthy);
            Assert.Equal(PolicyStateHealthStatus.Corrupt, result.Status);
            Assert.Equal("STATE_CORRUPT", result.Code);
            Assert.Equal(0, store.SaveCount);
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "SirkAgentTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class RecordingPolicyStateStore : IPolicyStateStore
    {
        private readonly string _path;

        public RecordingPolicyStateStore(string path) => _path = path;

        public int SaveCount { get; private set; }
        public PolicyState? SavedState { get; private set; }

        public PolicyState Load() => SavedState ?? PolicyState.Empty;

        public void Save(PolicyState state)
        {
            SaveCount++;
            SavedState = state;
            File.WriteAllBytes(_path, [42]);
        }
    }

    private sealed class ThrowingPolicyStateStore : IPolicyStateStore
    {
        private readonly Exception _exception;

        public ThrowingPolicyStateStore(Exception exception) => _exception = exception;

        public int SaveCount { get; private set; }

        public PolicyState Load() => throw _exception;

        public void Save(PolicyState state) => SaveCount++;
    }
}
