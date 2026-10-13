using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Rtfd.Processes;

public sealed record ProcessRequest
{
    /// <summary>A path, or a bare name looked up on the child's <c>PATH</c> (never the working directory).</summary>
    public required string FileName { get; init; }

    public required IReadOnlyList<string> Arguments { get; init; }

    /// <summary>The child's whole environment. Nothing is inherited.</summary>
    public required IReadOnlyDictionary<string, string> Environment { get; init; }

    public required string WorkingDirectory { get; init; }

    /// <summary>Written to stdin, then stdin is closed. Null closes it straight away.</summary>
    public string? Stdin { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Characters kept from each of stdout and stderr; the rest is read and dropped.</summary>
    public int MaxOutputChars { get; init; } = 16 * 1024 * 1024;
}

public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr)
{
    public bool TimedOut { get; init; }
    public bool StdoutTruncated { get; init; }

    /// <summary>Why the process didn't start (missing binary, not executable), or null.</summary>
    public string? StartError { get; init; }

    public bool Succeeded => StartError is null && !TimedOut && ExitCode == 0;
}

/// <summary>
/// Starts every child process except git, which goes through <see cref="Git.GitRunner"/>
/// (and from there through here). Arguments go in <c>ArgumentList</c>, never a joined string.
/// The environment is exactly what the request says. There is no window on Windows, and a
/// timeout kills the whole process tree.
/// </summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        var executable = Resolve(request.FileName, request.Environment);
        if (executable is null)
        {
            return new ProcessResult(-1, "", "") { StartError = $"'{request.FileName}' was not found on PATH" };
        }

        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var argument in request.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment.Clear();
        foreach (var (name, value) in request.Environment)
        {
            start.Environment[name] = value;
        }

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("Process.Start returned null");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return new ProcessResult(-1, "", "") { StartError = $"could not start '{executable}': {ex.Message}" };
        }

        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(request.Timeout);
            var stdout = new CappedReader(process.StandardOutput, request.MaxOutputChars);
            var stderr = new CappedReader(process.StandardError, request.MaxOutputChars);
            try
            {
                try
                {
                    if (request.Stdin is not null)
                    {
                        await process.StandardInput.WriteAsync(request.Stdin.AsMemory(), timeout.Token).ConfigureAwait(false);
                    }

                    process.StandardInput.Close();
                }
                catch (IOException)
                {
                    // The child exited without reading its input; its exit code says why.
                }

                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                await CappedReader.DrainAsync(stdout, stderr).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                return new ProcessResult(-1, stdout.Text, stderr.Text) { TimedOut = true };
            }

            await CappedReader.DrainAsync(stdout, stderr).ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, stdout.Text, stderr.Text) { StdoutTruncated = stdout.Truncated };
        }
    }

    /// <summary>
    /// Finds the executable on the child's own <c>PATH</c>. Doing it here, rather than letting
    /// the OS search, keeps the working directory out of the search. Windows batch files are
    /// refused: they need cmd.exe, which is a shell.
    /// </summary>
    public static string? Resolve(string fileName, IReadOnlyDictionary<string, string> environment)
    {
        var extensions = OperatingSystem.IsWindows()
            ? (environment.GetValueOrDefault("PATHEXT") ?? ".COM;.EXE").Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Where(e => !e.Equals(".BAT", StringComparison.OrdinalIgnoreCase) && !e.Equals(".CMD", StringComparison.OrdinalIgnoreCase))
                .Prepend("")
                .ToArray()
            : [""];

        if (fileName.Contains('/') || fileName.Contains(Path.DirectorySeparatorChar))
        {
            var full = Path.GetFullPath(fileName);
            return extensions.Select(e => full + e).FirstOrDefault(IsRunnable);
        }

        var path = environment.GetValueOrDefault("PATH") ?? "";
        foreach (var folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Path.IsPathFullyQualified(folder))
            {
                continue;
            }

            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(folder, fileName + extension);
                if (IsRunnable(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static bool IsRunnable(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            var extension = Path.GetExtension(path);
            return extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) || extension.Equals(".com", StringComparison.OrdinalIgnoreCase);
        }

        return (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Already gone.
        }
    }

    /// <summary>Reads a stream to its end on its own task, keeping the first characters only.</summary>
    private sealed class CappedReader
    {
        private readonly StringBuilder _text = new();
        private readonly int _max;
        private readonly Task _reading;

        public CappedReader(StreamReader reader, int max)
        {
            _max = max;
            _reading = ReadAsync(reader);
        }

        public bool Truncated { get; private set; }

        public string Text
        {
            get
            {
                lock (_text)
                {
                    return _text.ToString();
                }
            }
        }

        /// <summary>
        /// Waits for both streams to close, but not forever: a grandchild that outlived the
        /// tree kill can hold a pipe open. What was read by then is kept.
        /// </summary>
        public static async Task DrainAsync(CappedReader stdout, CappedReader stderr)
        {
            try
            {
                await Task.WhenAll(stdout._reading, stderr._reading).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
        }

        private async Task ReadAsync(StreamReader reader)
        {
            var buffer = new char[16 * 1024];
            try
            {
                int read;
                while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                {
                    lock (_text)
                    {
                        var room = _max - _text.Length;
                        if (room > 0)
                        {
                            _text.Append(buffer, 0, Math.Min(room, read));
                        }

                        Truncated |= read > room;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // The process was killed and its pipe went away.
            }
        }
    }
}
