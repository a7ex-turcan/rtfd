using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rtfd.GitTools;

/// <summary>
/// What one <c>rtfd-git</c> server is bound to: the review's mirror, worktree and commits.
/// rtfd writes it into the run folder before the agent starts, and the server reads it from
/// there, never from the agent (plan 1c.2). The agent can read this file but not write it.
/// </summary>
/// <param name="LastReviewedHead">On a follow-up, the head the last review was of.</param>
/// <param name="GitDirectory">The data directory's <c>git</c> folder, for <see cref="Git.GitRunner"/>.</param>
public sealed record GitToolsBinding(string Mirror, string Worktree, string Head, string MergeBase, string? LastReviewedHead, string GitDirectory)
{
    public const string FileName = "git-tools.json";

    /// <summary>On a follow-up, the last reviewed head's merge base: its commits since are allowed too, rebased or not.</summary>
    public string? LastMergeBase { get; init; }

    public void Write(string runFolder) =>
        File.WriteAllText(Path.Combine(runFolder, FileName), JsonSerializer.Serialize(this, GitToolsJson.Default.GitToolsBinding));

    public static GitToolsBinding Read(string runFolder)
    {
        var path = Path.Combine(runFolder, FileName);
        return JsonSerializer.Deserialize(File.ReadAllText(path), GitToolsJson.Default.GitToolsBinding)
            ?? throw new InvalidDataException($"{path} is empty");
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true, NewLine = "\n")]
[JsonSerializable(typeof(GitToolsBinding))]
internal sealed partial class GitToolsJson : JsonSerializerContext;
