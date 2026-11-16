using MuseLauncher.Core;
using Xunit;

namespace MuseLauncher.Core.Tests;

public sealed class LaunchCommandsRegressionTest
{
    [Theory]
    [InlineData("123")]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData(" 123 ")]
    [InlineData("")]
    public void StringFields_NeverConvertToBooleanOrLong(string value)
    {
        var equals = LaunchCommands.Parse(new[] { "--search_query=" + value, "--proxy_preset=" + value });
        var spaced = LaunchCommands.Parse(new[] { "--search_query", value, "--proxy_preset", value });
        Assert.Equal(value, equals.SearchQuery);
        Assert.Equal(value, equals.ProxyPreset);
        Assert.Equal(value, spaced.SearchQuery);
        Assert.Equal(value, spaced.ProxyPreset);
    }

    [Theory]
    [InlineData("show_drawer")]
    [InlineData("dump_auth")]
    [InlineData("add_account")]
    [InlineData("proxy_autotest")]
    [InlineData("proxy_save")]
    public void SpaceSeparatedFalse_DisablesBooleanFlags(string key)
    {
        var cmd = LaunchCommands.Parse(new[] { "--" + key, "false" });
        Assert.False(cmd.ShowDrawer || cmd.DumpAuth || cmd.AddAccount || cmd.ProxyAutoTest || cmd.ProxySave);
    }

    [Fact]
    public void MissingLongValue_DoesNotSwallowNextOption()
    {
        var cmd = LaunchCommands.Parse(new[] { "--start_account", "--add_account" });
        Assert.Equal(-1L, cmd.StartAccount);
        Assert.True(cmd.AddAccount);
    }

    [Fact]
    public void BareOpenConfig_DoesNotSwallowNextOption()
    {
        var cmd = LaunchCommands.Parse(new[] { "--open_config", "--dump_auth" });
        Assert.True(cmd.HasOpenConfig);
        Assert.Equal(-1L, cmd.OpenConfigId);
        Assert.True(cmd.DumpAuth);
    }

    [Fact]
    public void UnknownOption_DoesNotSwallowKnownOption()
    {
        var cmd = LaunchCommands.Parse(new[] { "--unknown", "--add_account" });
        Assert.True(cmd.AddAccount);
    }

    [Fact]
    public void MissingStringValue_KeepsNullAndDoesNotSwallowNextOption()
    {
        var cmd = LaunchCommands.Parse(new[] { "--search_query", "--show_drawer" });
        Assert.Null(cmd.SearchQuery);
        Assert.True(cmd.ShowDrawer);
    }

    [Fact]
    public void BareFullscreen_DoesNotSwallowNextOption()
    {
        var cmd = LaunchCommands.Parse(new[] { "--fullscreen", "--show_drawer" });
        Assert.True(cmd.HasFullscreen);
        Assert.True(cmd.Fullscreen);
        Assert.True(cmd.ShowDrawer);
    }
}
