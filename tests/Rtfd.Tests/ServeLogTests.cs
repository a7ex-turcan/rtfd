using Rtfd.Serve;
using Rtfd.Tests.Support;

namespace Rtfd.Tests;

/// <summary>serve's daily log files (plan Phase 5).</summary>
public sealed class ServeLogTests : IDisposable
{
    private readonly TempHome _home = new();

    public void Dispose() => _home.Dispose();

    [Fact]
    public void Lines_go_to_the_day_s_file_with_a_UTC_time_and_old_files_are_dropped()
    {
        var folder = Path.Combine(_home.Root, "logs");
        Directory.CreateDirectory(folder);
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 15, 23, 59, 30, TimeSpan.FromHours(3)));
        var log = new ServeLog(folder, clock);
        var old = log.PathFor(new DateOnly(2026, 9, 1));
        var recent = log.PathFor(new DateOnly(2026, 10, 10));
        File.WriteAllText(old, "");
        File.WriteAllText(recent, "");
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "");

        log.Write("nutrx: 2 open PRs, nothing to do");
        using (var writer = log.For("nutrx #12: "))
        {
            writer.WriteLine("Reading PR #12 from owner/nutrx...");
            writer.Write("Attempt 1 of 3: ");
            writer.Write("running claude...\r\n");
        }

        Assert.Equal(
            ["2026-10-15T20:59:30Z nutrx: 2 open PRs, nothing to do", "2026-10-15T20:59:30Z nutrx #12: Reading PR #12 from owner/nutrx...", "2026-10-15T20:59:30Z nutrx #12: Attempt 1 of 3: running claude..."],
            File.ReadAllLines(log.PathFor(new DateOnly(2026, 10, 15))));
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(Path.Combine(folder, "notes.txt")));
    }
}
