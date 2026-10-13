namespace Rtfd.Config;

public enum DiagnosticLevel
{
    Warning,
    Error,
}

/// <summary>One finding about the config, from loading it or from a check, as in rtfq.</summary>
/// <param name="Check">
/// The named check that produced this, such as <c>config.hooks_unsupported</c>. Named rather
/// than anonymous so a diagnostic can be cited in docs and matched in tests.
/// </param>
/// <param name="Level">Whether it stops rtfd from starting.</param>
/// <param name="Message">What is wrong, for a person.</param>
/// <param name="Path">Dotted path into the config, such as <c>repos[0].token_env</c>.</param>
/// <param name="Line">1-based line in the config file, or 0 when unknown.</param>
public sealed record Diagnostic(string Check, DiagnosticLevel Level, string Message, string Path = "", int Line = 0)
{
    public bool IsError => Level == DiagnosticLevel.Error;

    public override string ToString()
    {
        var where = (Line > 0, Path.Length > 0) switch
        {
            (true, true) => $"line {Line}, {Path}",
            (true, false) => $"line {Line}",
            (false, true) => Path,
            _ => "",
        };
        var prefix = IsError ? "error" : "warning";
        return where.Length > 0 ? $"{prefix} [{Check}] {where}: {Message}" : $"{prefix} [{Check}]: {Message}";
    }
}
