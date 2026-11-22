using Mindweave.Application;
using Mindweave.Desktop.ViewModels.Pages;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// A release build shows only the product name and version; other channels also say which build they are (commit and
/// its date) and which data they use (owner, 2026-11-22).
/// </summary>
public class ReleaseChannelTests
{
    private static AppEnvironment Env(string channel, string? commit = "5f50734", string? date = "2026-11-22") =>
        new(channel, "0.2.0", "/data", "/data/mindweave.db", "/data/backups", "/data/logs", 17, commit, date);

    [Fact]
    public void The_release_channel_is_not_named_in_settings_nor_its_commit()
    {
        Assert.Equal("Mindweave 0.2.0", SettingsHomeViewModel.VersionTextFor(Env(AppEnvironment.ReleaseChannel)));
    }

    [Theory]
    [InlineData("dev")]
    [InlineData("claude-auto")]
    public void Other_channels_name_the_build_and_the_channel_in_settings(string channel)
    {
        Assert.Equal($"Mindweave 0.2.0 · 5f50734 · 2026-11-22 · 数据通道 {channel}",
            SettingsHomeViewModel.VersionTextFor(Env(channel)));
    }

    [Fact]
    public void A_build_without_a_commit_shows_the_version_and_channel_only()
    {
        Assert.Equal("Mindweave 0.2.0 · 数据通道 dev", SettingsHomeViewModel.VersionTextFor(Env("dev", null, null)));
    }

    [Theory]
    [InlineData("0.2.0+5f507342892602d452276952331f4c7a387e4a4f", " 2026-11-22\n", "0.2.0", "5f50734", "2026-11-22")]
    [InlineData("0.2.0", null, "0.2.0", null, null)]
    [InlineData("0.2.0+", "", "0.2.0", null, null)]
    [InlineData("0.2.0+abc", null, "0.2.0", "abc", null)]
    [InlineData(null, null, "0.0.0", null, null)]
    public void The_build_stamp_reads_version_commit_and_date(
        string? informational, string? date, string version, string? commit, string? commitDate)
    {
        Assert.Equal(new BuildStamp(version, commit, commitDate), BuildStamp.Parse(informational, date));
    }

    [Fact]
    public void This_build_is_stamped_with_a_short_commit_and_its_date_when_built_from_git()
    {
        var stamp = BuildStamp.Read(typeof(CompositionRoot).Assembly);

        Assert.Matches(@"^\d+\.\d+\.\d+$", stamp.Version);
        if (stamp.Commit is not null)
            Assert.Matches("^[0-9a-f]{7}$", stamp.Commit);
        if (stamp.CommitDate is not null)
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", stamp.CommitDate);
        // Tests run from a git checkout (locally and on CI), so both must be there; this also checks the Windows
        // escaping of the date format in Mindweave.Desktop.csproj.
        Assert.NotNull(stamp.Commit);
        Assert.NotNull(stamp.CommitDate);
    }
}
