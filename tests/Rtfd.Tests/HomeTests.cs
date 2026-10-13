namespace Rtfd.Tests;

public class HomeTests
{
    [Fact]
    public void The_home_is_RTFD_HOME_when_it_is_set()
    {
        var root = Path.Combine(Path.GetTempPath(), "somewhere");

        var home = RtfdHome.Resolve(name => name == RtfdHome.EnvironmentVariable ? root : null);

        Assert.Equal(Path.GetFullPath(root), home.Root);
        Assert.Equal(Path.Combine(Path.GetFullPath(root), "rtfd.yaml"), home.ConfigPath);
    }

    [Fact]
    public void The_home_defaults_to_dot_rtfd_in_the_user_profile()
    {
        var home = RtfdHome.Resolve(_ => null);

        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".rtfd"), home.Root);
    }

    [Fact]
    public void The_data_directory_defaults_to_the_home()
    {
        using var temp = new TempHome();

        var loaded = temp.Load(Configs.Minimal());

        Assert.Equal(temp.Root, loaded.Config!.Data.Root);
    }

    [Fact]
    public void A_relative_data_dir_resolves_against_the_config_folder()
    {
        using var temp = new TempHome();

        var loaded = temp.Load(Configs.Minimal(defaults: "  data_dir: data"));

        Assert.Equal(Path.Combine(temp.Root, "data"), loaded.Config!.Data.Root);
    }

    [Fact]
    public void A_data_dir_starting_with_a_tilde_is_in_the_user_profile()
    {
        using var temp = new TempHome();

        var loaded = temp.Load(Configs.Minimal(defaults: "  data_dir: ~/rtfd-data"));

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine(profile, "rtfd-data"), loaded.Config!.Data.Root);
    }
}
