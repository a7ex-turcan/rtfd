using System.Globalization;
using System.Text;

namespace Rtfd.Serve;

/// <summary>
/// <c>serve</c>'s log (plan Phase 5): one line per event, a UTC timestamp first, in a file per day
/// (<c>logs/rtfd-yyyy-MM-dd.log</c> in the data directory), kept for <see cref="KeepDays"/> days.
/// Every poll writes a line, so a quiet log means a dead loop. Lines are echoed to the console
/// too, when there is one. Logging never takes serve down: a write that fails is dropped.
/// </summary>
public sealed class ServeLog(string folder, TimeProvider clock, TextWriter? echo = null)
{
    public const int KeepDays = 14;

    private readonly Lock _lock = new();
    private DateOnly _pruned;

    public string Folder { get; } = folder;

    public string PathFor(DateOnly day) => Path.Combine(Folder, $"rtfd-{day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.log");

    public void Write(string message)
    {
        var now = clock.GetUtcNow();
        var line = $"{now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)} {message}";
        lock (_lock)
        {
            var today = DateOnly.FromDateTime(now.UtcDateTime);
            try
            {
                Directory.CreateDirectory(Folder);
                if (_pruned != today)
                {
                    Prune(today);
                    _pruned = today;
                }

                File.AppendAllText(PathFor(today), line + "\n");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The console still gets the line.
            }

            echo?.WriteLine(line);
        }
    }

    /// <summary>A writer whose every line goes to the log with <paramref name="prefix"/> in front: a run's progress, for one repo.</summary>
    public TextWriter For(string prefix) => new PrefixWriter(this, prefix);

    private void Prune(DateOnly today)
    {
        var oldest = today.AddDays(-KeepDays + 1);
        foreach (var file in Directory.EnumerateFiles(Folder, "rtfd-*.log"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (DateOnly.TryParseExact(name["rtfd-".Length..], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) && day < oldest)
            {
                File.Delete(file);
            }
        }
    }

    private sealed class PrefixWriter(ServeLog log, string prefix) : TextWriter
    {
        private readonly StringBuilder _line = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            if (value == '\n')
            {
                Flush();
            }
            else if (value != '\r')
            {
                _line.Append(value);
            }
        }

        public override void Write(string? value)
        {
            foreach (var c in value ?? "")
            {
                Write(c);
            }
        }

        public override void WriteLine(string? value)
        {
            Write(value);
            Flush();
        }

        public override void Flush()
        {
            if (_line.Length > 0)
            {
                log.Write($"{prefix}{_line}");
                _line.Clear();
            }
        }
    }
}
