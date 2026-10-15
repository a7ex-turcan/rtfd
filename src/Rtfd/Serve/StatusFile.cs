using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rtfd.Serve;

/// <summary>
/// <c>status.json</c> in the data directory (plan Phase 5): what <c>serve</c> is doing, rewritten
/// atomically every heartbeat and after every tick. <c>rtfd status</c> reads it with the store.
/// Times are ISO 8601 UTC, for programs as well as people.
/// </summary>
/// <param name="Heartbeat">When this was written. Older than two intervals means serve isn't running, or is hung.</param>
/// <param name="StoppedAt">Set by a serve that stopped cleanly, as it left.</param>
/// <param name="ConfigChanged">rtfd.yaml changed since serve started; a restart applies it.</param>
public sealed record ServeStatus(
    int Version, int Pid, string RtfdVersion, string Config, string StartedAt, string Heartbeat, string? StoppedAt, bool ConfigChanged,
    List<RepoStatus> Repos, List<AccountStatus> Accounts)
{
    public const int CurrentVersion = 1;
}

/// <param name="State">watching, paused or stopped.</param>
/// <param name="Reason">Why it is stopped, and what fixes it.</param>
public sealed record RepoStatus(
    string Name, string Slug, string Agent, string Account, int IntervalSeconds, string State, string? Reason,
    string? LastPoll, string? LastPollResult, string? NextPoll, ReviewingStatus? Reviewing, List<PrStatus> Prs);

public sealed record ReviewingStatus(int Number, string Head, string Since);

/// <param name="State">reviewed, quiet, waiting_for_ci, due, reviewing, skipped, blocked, attempts_used_up, backing_off, nothing_to_review or account_paused.</param>
/// <param name="Detail">What the state means for this PR, for a person.</param>
/// <param name="Until">When a wait ends.</param>
public sealed record PrStatus(
    int Number, string Title, string Author, string Head, string State, string? Detail, string? Until,
    int AttemptsUsed, int AttemptsLeft, LastReviewStatus? LastReview);

public sealed record LastReviewStatus(string Head, string Event, string? Url, string At);

public sealed record AccountStatus(string Name, string PausedUntil, string? Message);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true, NewLine = "\n")]
[JsonSerializable(typeof(ServeStatus))]
internal sealed partial class StatusJson : JsonSerializerContext;

public static class StatusFile
{
    /// <summary>Writes beside the file and moves it over, so a reader sees the old status or the new, never half of one.</summary>
    public static void Write(string path, ServeStatus status)
    {
        var temp = path + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(temp, JsonSerializer.Serialize(status, StatusJson.Default.ServeStatus) + "\n");
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 10)
            {
                // A reader on Windows holds the old one open for a moment.
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>The status serve last wrote, or null when there is none or it can't be read.</summary>
    public static ServeStatus? Read(string path)
    {
        try
        {
            // Shared for delete, so serve's next move over this file isn't held up by the read.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize(stream, StatusJson.Default.ServeStatus);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
