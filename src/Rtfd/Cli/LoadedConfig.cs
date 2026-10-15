using Rtfd.Config;

namespace Rtfd.Cli;

/// <summary>The config a command names with <c>--config</c>, or the home's.</summary>
internal static class LoadedConfig
{
    /// <summary>The config with its checks passed, or null after saying why it can't be used.</summary>
    /// <param name="checks">Run the checks too: a command that starts work refuses a config with errors.</param>
    public static RtfdConfig? Load(CommandContext ctx, CommandLine commandLine, string command, bool checks = true)
    {
        var home = ctx.Home;
        var path = commandLine.Value("config") is { } given ? Path.GetFullPath(given) : home.ConfigPath;
        if (!File.Exists(path))
        {
            ctx.Stderr.WriteLine($"rtfd {command}: no config at {path}. Run `rtfd validate` for help setting one up.");
            return null;
        }

        var loaded = ConfigLoader.LoadFile(path, home);
        var diagnostics = loaded.Config is null || !checks ? loaded.Diagnostics : [.. loaded.Diagnostics, .. ConfigChecks.Run(loaded.Config, ctx.Environment)];
        var errors = diagnostics.Where(d => d.IsError).ToList();
        if (loaded.Config is not { } config || (checks && errors.Count > 0))
        {
            ctx.Stderr.WriteLine($"rtfd {command}: the config has {errors.Count} error{(errors.Count == 1 ? "" : "s")}; `rtfd validate` lists them.");
            foreach (var error in errors)
            {
                ctx.Stderr.WriteLine($"  {error}");
            }

            return null;
        }

        return config;
    }

    /// <summary>The repo the first positional names, or null after saying which ones there are.</summary>
    public static RepoConfig? Repo(CommandContext ctx, RtfdConfig config, CommandLine commandLine, string command)
    {
        var name = commandLine.Positionals[0];
        if (config.Repos.FirstOrDefault(r => r.Name == name) is { } repo)
        {
            return repo;
        }

        ctx.Stderr.WriteLine($"rtfd {command}: no repo named '{name}' in {config.FilePath}. It has: {string.Join(", ", config.Repos.Select(r => r.Name))}.");
        return null;
    }
}
