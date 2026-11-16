using Microsoft.Data.Sqlite;

namespace MuseLauncher.Core;

/// <summary>只读取 cookie 名的独立快照，未知会话保持未知，不读 cookie 值。</summary>
public static class CookieProbe
{
    public const string AuthCookieName = "hatch_sess";
    private static readonly DateTimeOffset ChromiumEpoch = new(1601, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static bool IsLogged(IReadOnlyList<string>? names) => names?.Contains(AuthCookieName) == true;

    public static List<string>? ProbeCookieNamesOnDisk(string profileRoot, DateTimeOffset? now = null)
    {
        var candidates = new[]
        {
            Path.Combine(profileRoot, "EBWebView", "Default", "Network", "Cookies"),
            Path.Combine(profileRoot, "EBWebView", "Default", "Cookies"),
            Path.Combine(profileRoot, "Default", "Network", "Cookies"),
            Path.Combine(profileRoot, "Default", "Cookies"),
        };
        var source = candidates.FirstOrDefault(File.Exists);
        if (source == null) return null;
        var temporary = Path.Combine(Path.GetTempPath(), "MuseLauncherProbe-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temporary);
            var snapshot = Path.Combine(temporary, "Cookies");
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(source + suffix)) File.Copy(source + suffix, snapshot + suffix, overwrite: false);
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = snapshot,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            connection.Open();
            var timestamp = checked(((now ?? DateTimeOffset.UtcNow) - ChromiumEpoch).Ticks / 10);
            using var query = connection.CreateCommand();
            query.CommandText = "SELECT name, expires_utc, is_persistent FROM cookies "
                + "WHERE (host_key = 'muse.ai' OR host_key = '.muse.ai') AND path = '/'";
            var names = new List<string>();
            using var reader = query.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.GetString(0);
                var expiry = reader.GetInt64(1);
                var persistent = reader.GetInt64(2) != 0;
                if (name == AuthCookieName && (!persistent || expiry == 0)) return null;
                if (persistent && expiry > timestamp) names.Add(name);
            }
            return names;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or OverflowException)
        {
            return null;
        }
        finally
        {
            try { if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
