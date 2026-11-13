using Mindweave.Infrastructure.Hosting;

namespace Mindweave.Infrastructure.Tests;

public class AppPathsTests
{
    [Fact]
    public void Channel_is_part_of_the_default_directory()
    {
        var a = AppPaths.Resolve("claude-auto", _ => null);
        var b = AppPaths.Resolve("codex-steady", _ => null);

        Assert.EndsWith(Path.Combine("Mindweave", "claude-auto"), a.DataDirectory, StringComparison.Ordinal);
        Assert.NotEqual(a.DataDirectory, b.DataDirectory);
        Assert.NotEqual(a.InstanceKey, b.InstanceKey);
    }

    [Fact]
    public void Environment_override_replaces_the_whole_directory()
    {
        var target = Path.Combine(Path.GetTempPath(), "mw-override");
        var paths = AppPaths.Resolve("claude-auto", name => name == AppPaths.DataDirectoryVariable ? target : null);

        Assert.Equal(Path.GetFullPath(target), paths.DataDirectory);
        Assert.Equal(Path.Combine(paths.DataDirectory, "mindweave.db"), paths.DatabasePath);
    }

    [Fact]
    public void Channel_is_stamped_into_built_assemblies()
    {
        // Builds default to the dev channel; only scripts/daily.sh stamps the daily channel "claude-auto".
        Assert.Equal("claude-auto-dev", AppPaths.ChannelOf(typeof(AppPaths).Assembly));
    }
}
