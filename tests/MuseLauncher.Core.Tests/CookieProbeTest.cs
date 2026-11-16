using Microsoft.Data.Sqlite;
using MuseLauncher.Core;
using Xunit;

namespace MuseLauncher.Core.Tests;

[CollectionDefinition("SyntheticCookieProbe", DisableParallelization = true)]
public sealed class SyntheticCookieProbeCollection { }

[Collection("SyntheticCookieProbe")]
public sealed class CookieProbeTest : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "MuseLauncherCoreTests", Guid.NewGuid().ToString("N"));
    private readonly string profile;
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
    private static long Micros(DateTimeOffset date) => checked(date.ToUnixTimeMilliseconds() * 1000 + 11644473600000000L);

    public CookieProbeTest()
    {
        Directory.CreateDirectory(root);
        profile = Path.Combine(root, "synthetic_" + Guid.NewGuid().ToString("N"));
    }

    private string WriteCookie(string name = CookieProbe.AuthCookieName, string host = ".muse.ai", long? expires = null,
        int hasExpires = 1, int persistent = 1, string path = "/", string relative = "Default/Network/Cookies")
    {
        var db = Path.Combine(profile, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(db)!);
        if (File.Exists(db)) File.Delete(db);
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Pooling = false }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE cookies(name TEXT, host_key TEXT, path TEXT, expires_utc INTEGER, has_expires INTEGER, is_persistent INTEGER);"
            + "INSERT INTO cookies VALUES($name,$host,$path,$expires,$hasExpires,$persistent);";
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$host", host);
        cmd.Parameters.AddWithValue("$path", path);
        cmd.Parameters.AddWithValue("$expires", expires ?? Micros(Now.AddDays(1)));
        cmd.Parameters.AddWithValue("$hasExpires", hasExpires);
        cmd.Parameters.AddWithValue("$persistent", persistent);
        cmd.ExecuteNonQuery();
        return db;
    }

    [Fact]
    public void FreshProbe_SeesReplacedSourceInsteadOfPooledOldSnapshot()
    {
        WriteCookie();
        Assert.True(CookieProbe.IsLogged(CookieProbe.ProbeCookieNamesOnDisk(profile, Now)));
        WriteCookie(name: "_ga");
        var second = CookieProbe.ProbeCookieNamesOnDisk(profile, Now);
        Assert.NotNull(second);
        Assert.DoesNotContain(CookieProbe.AuthCookieName, second);
        Assert.Contains("_ga", second);
    }

    [Theory]
    [InlineData("notmuse.ai")]
    [InlineData("muse.ai.example.org")]
    [InlineData("sub.muse.ai")]
    [InlineData(".sub.muse.ai")]
    public void UnrelatedOrChildHost_DoesNotAuthenticateRootHost(string host)
    {
        WriteCookie(host: host);
        var names = CookieProbe.ProbeCookieNamesOnDisk(profile, Now);
        Assert.NotNull(names);
        Assert.False(CookieProbe.IsLogged(names));
    }

    [Theory]
    [InlineData("muse.ai")]
    [InlineData(".muse.ai")]
    public void ExactRootHostOrDomain_WithFuturePersistentCookie_IsLogged(string host)
    {
        WriteCookie(host: host);
        Assert.True(CookieProbe.IsLogged(CookieProbe.ProbeCookieNamesOnDisk(profile, Now)));
    }

    [Fact]
    public void ExpiredCookie_DoesNotAuthenticate()
    {
        WriteCookie(expires: Micros(Now.AddSeconds(-1)));
        Assert.False(CookieProbe.IsLogged(CookieProbe.ProbeCookieNamesOnDisk(profile, Now)));
    }

    [Fact]
    public void CookieExpiringNow_DoesNotAuthenticate()
    {
        WriteCookie(expires: Micros(Now));
        Assert.False(CookieProbe.IsLogged(CookieProbe.ProbeCookieNamesOnDisk(profile, Now)));
    }

    [Fact]
    public void SessionAuthCookie_OnDiskIsUnknownNotLogged()
    {
        WriteCookie(expires: 0, hasExpires: 0, persistent: 0);
        Assert.Null(CookieProbe.ProbeCookieNamesOnDisk(profile, Now));
    }

    [Fact]
    public void CookieForDifferentPath_DoesNotAuthenticateRootPath()
    {
        WriteCookie(path: "/private");
        Assert.False(CookieProbe.IsLogged(CookieProbe.ProbeCookieNamesOnDisk(profile, Now)));
    }

    [Theory]
    [InlineData("EBWebView/Default/Network/Cookies")]
    [InlineData("EBWebView/Default/Cookies")]
    [InlineData("Default/Network/Cookies")]
    [InlineData("Default/Cookies")]
    public void SupportedWebViewLayouts_AreProbed(string relative)
    {
        WriteCookie(relative: relative);
        Assert.True(CookieProbe.IsLogged(CookieProbe.ProbeCookieNamesOnDisk(profile, Now)));
    }

    public void Dispose()
    {
        // This test process only uses synthetic cookie databases. Release legacy pooled handles
        // so the red-stage implementation cannot leave a synthetic snapshot on Windows.
        SqliteConnection.ClearAllPools();
        var legacySnapshot = Path.Combine(Path.GetTempPath(), "MuseLauncherProbe", Path.GetFileName(profile));
        try { Directory.Delete(legacySnapshot, recursive: true); } catch { }
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}
