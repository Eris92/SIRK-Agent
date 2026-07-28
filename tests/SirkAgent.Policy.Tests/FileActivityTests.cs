using SirkAgent.Service;
using Xunit;

namespace SirkAgent.Policy.Tests;

public sealed class FileActivityTests
{
    [Fact]
    public void Detects_create_change_and_delete()
    {
        var time = DateTime.UtcNow;
        var previous = new Dictionary<string, FileFingerprint>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\case\changed.txt"] = new(10, time, "OLD"),
            [@"C:\case\deleted.txt"] = new(20, time, "DELETE")
        };
        var current = new Dictionary<string, FileFingerprint>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\case\changed.txt"] = new(11, time.AddSeconds(1), "NEW"),
            [@"C:\case\created.txt"] = new(30, time, "CREATE")
        };

        var result = FileActivityWorker.Diff(previous, current);

        Assert.Contains(result, value => value.Action == "Create" && value.Path.EndsWith("created.txt"));
        Assert.Contains(result, value => value.Action == "Change" && value.Path.EndsWith("changed.txt"));
        Assert.Contains(result, value => value.Action == "Delete" && value.Path.EndsWith("deleted.txt"));
    }
}
