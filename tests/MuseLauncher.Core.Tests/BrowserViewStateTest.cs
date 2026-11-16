using Xunit;

namespace MuseLauncher.Core.Tests;

public class BrowserViewStateTest
{
    [Fact]
    public void BoundWebViewStaysHiddenUntilSplashEnds()
    {
        var state = new BrowserViewState();
        var version = state.Begin(1, showSplash: true);
        Assert.True(state.BindHost(1, version));
        Assert.False(state.CanShowWebView);
        Assert.True(state.HideSplash(1, version));
        Assert.True(state.CanShowWebView);
    }

    [Fact]
    public void ErrorAndSplashCannotExposeUnderlyingWebView()
    {
        var state = new BrowserViewState();
        var version = state.Begin(1, showSplash: true);
        state.BindHost(1, version);
        state.SetError(1, version, "连接失败");
        state.HideSplash(1, version);
        Assert.False(state.CanShowWebView);
        state.SetError(1, version, null);
        Assert.True(state.CanShowWebView);
    }

    [Fact]
    public void LateTimerAndNavigationCannotModifyNewAccount()
    {
        var state = new BrowserViewState();
        var first = state.Begin(1, showSplash: true);
        state.BindHost(1, first);
        var second = state.Begin(2, showSplash: true);
        Assert.False(state.HideSplash(1, first));
        Assert.False(state.SetError(1, first, "旧账号失败"));
        Assert.False(state.BindHost(1, first));
        Assert.True(state.SplashVisible);
        Assert.Null(state.Error);
        Assert.False(state.HasHost);
        state.BindHost(2, second);
        Assert.False(state.CanShowWebView);
    }

    [Fact]
    public void FailedCreationCannotShowPreviousAccount()
    {
        var state = new BrowserViewState();
        var first = state.Begin(1, showSplash: false);
        state.BindHost(1, first);
        Assert.True(state.CanShowWebView);
        var second = state.Begin(2, showSplash: true);
        state.SetError(2, second, "初始化失败");
        Assert.False(state.CanShowWebView);
        Assert.False(state.HasHost);
        Assert.Equal(2, state.AccountId);
    }

    [Fact]
    public void ClosingRejectsNewAndLateWork()
    {
        var state = new BrowserViewState();
        var version = state.Begin(1, showSplash: true);
        state.Close();
        Assert.False(state.BindHost(1, version));
        Assert.False(state.HideSplash(1, version));
        Assert.False(state.CanShowWebView);
        Assert.Throws<ObjectDisposedException>(() => state.Begin(2, showSplash: true));
    }
}
