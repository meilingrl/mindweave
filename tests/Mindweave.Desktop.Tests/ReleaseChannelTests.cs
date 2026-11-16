using Mindweave.Application;
using Mindweave.Desktop.ViewModels.Pages;

namespace Mindweave.Desktop.Tests;

/// <summary>A release build shows only the product name and version; other channels say which data they use.</summary>
public class ReleaseChannelTests
{
    private static AppEnvironment Env(string channel) => new(channel, "0.1.0", "/data", "/data/mindweave.db", "/data/backups", "/data/logs", 13);

    [Fact]
    public void The_release_channel_is_not_named_in_settings()
    {
        Assert.Equal("Mindweave 0.1.0", SettingsHomeViewModel.VersionTextFor(Env(AppEnvironment.ReleaseChannel)));
    }

    [Theory]
    [InlineData("dev")]
    [InlineData("claude-auto")]
    public void Other_channels_are_named_in_settings(string channel)
    {
        Assert.Equal($"Mindweave 0.1.0 · 数据通道 {channel}", SettingsHomeViewModel.VersionTextFor(Env(channel)));
    }
}
