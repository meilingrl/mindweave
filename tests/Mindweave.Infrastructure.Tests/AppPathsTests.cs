using Mindweave.Infrastructure.Hosting;

namespace Mindweave.Infrastructure.Tests;

public class AppPathsTests
{
    [Fact]
    public void Channel_is_part_of_the_default_directory()
    {
        var a = AppPaths.Resolve("daily", _ => null);
        var b = AppPaths.Resolve("dev", _ => null);

        Assert.EndsWith(Path.Combine("Mindweave", "daily"), a.DataDirectory, StringComparison.Ordinal);
        Assert.NotEqual(a.DataDirectory, b.DataDirectory);
        Assert.NotEqual(a.InstanceKey, b.InstanceKey);
    }

    [Fact]
    public void Environment_override_replaces_the_whole_directory()
    {
        var target = Path.Combine(Path.GetTempPath(), "mw-override");
        var paths = AppPaths.Resolve("daily", name => name == AppPaths.DataDirectoryVariable ? target : null);

        Assert.Equal(Path.GetFullPath(target), paths.DataDirectory);
        Assert.Equal(Path.Combine(paths.DataDirectory, "mindweave.db"), paths.DatabasePath);
    }

    [Fact]
    public void Channel_is_stamped_into_built_assemblies()
    {
        // Without an explicit channel, Debug builds use "dev" and Release builds the release channel, so a dev run
        // never opens the data of a published build. scripts/daily.sh stamps its own channel explicitly.
#if DEBUG
        Assert.Equal("dev", AppPaths.ChannelOf(typeof(AppPaths).Assembly));
#else
        Assert.Equal(Mindweave.Application.AppEnvironment.ReleaseChannel, AppPaths.ChannelOf(typeof(AppPaths).Assembly));
#endif
    }
}
