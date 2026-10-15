namespace Rtfd.Serve;

/// <summary>
/// An exclusive lock on a file, as rtfc's daemon takes one: <see cref="FileShare.None"/> is an
/// exclusive share mode on Windows and an exclusive <c>flock</c> on Unix, and the operating system
/// releases it when the process ends, crash or not. The file itself stays; deleting a lock file
/// someone else is about to open is how two holders happen.
/// </summary>
/// <remarks>
/// Two locks use it (plan Phase 5): <see cref="DataDirectory.ServeLockPath"/>, one <c>serve</c> per
/// data directory, and <see cref="DataDirectory.RepoLockPath"/>, so a manual <c>rtfd run</c> and
/// <c>serve</c> never work on one repo's mirror and PRs at once.
/// </remarks>
public sealed class FileLock : IDisposable
{
    private readonly FileStream _stream;

    private FileLock(FileStream stream) => _stream = stream;

    /// <summary>The lock, or null while someone else holds it.</summary>
    public static FileLock? TryAcquire(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        try
        {
            return new FileLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Waits up to <paramref name="wait"/> for the lock: a poll lets go of a repo in a second or two.</summary>
    public static async Task<FileLock?> AcquireAsync(string path, TimeSpan wait, CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + wait;
        while (true)
        {
            if (TryAcquire(path) is { } held)
            {
                return held;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return null;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Whether someone holds the lock now. Takes it for an instant when nobody does.</summary>
    public static bool IsHeld(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        using var probe = TryAcquire(path);
        return probe is null;
    }

    public void Dispose() => _stream.Dispose();
}
