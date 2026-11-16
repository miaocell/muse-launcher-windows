namespace MuseLauncher.Core;

/// <summary>当前页面的展示状态。旧切换的回调不能改变新账号的遮罩或可见性。</summary>
public sealed class BrowserViewState
{
    public long Version { get; private set; }
    public long? AccountId { get; private set; }
    public bool HasHost { get; private set; }
    public bool SplashVisible { get; private set; }
    public string? Error { get; private set; }
    public bool IsClosing { get; private set; }
    public bool CanShowWebView => !IsClosing && HasHost && !SplashVisible && Error == null;

    public long Begin(long accountId, bool showSplash)
    {
        ObjectDisposedException.ThrowIf(IsClosing, this);
        if (accountId <= 0) throw new ArgumentOutOfRangeException(nameof(accountId));
        Version = checked(Version + 1);
        AccountId = accountId;
        HasHost = false;
        SplashVisible = showSplash;
        Error = null;
        return Version;
    }

    public bool Matches(long accountId, long version)
        => !IsClosing && AccountId == accountId && Version == version;

    public bool BindHost(long accountId, long version)
    {
        if (!Matches(accountId, version)) return false;
        HasHost = true;
        return true;
    }

    public bool SetError(long accountId, long version, string? error)
    {
        if (!Matches(accountId, version)) return false;
        Error = error;
        if (error != null) SplashVisible = false;
        return true;
    }

    public bool HideSplash(long accountId, long version)
    {
        if (!Matches(accountId, version)) return false;
        SplashVisible = false;
        return true;
    }

    public void Clear()
    {
        Version = checked(Version + 1);
        AccountId = null;
        HasHost = false;
        SplashVisible = false;
        Error = null;
    }

    public void Close()
    {
        Clear();
        IsClosing = true;
    }
}
