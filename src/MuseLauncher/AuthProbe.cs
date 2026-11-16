using MuseLauncher.Core;

namespace MuseLauncher;

/// <summary>WPF 适配层，磁盘快照及有效期规则由可跨平台测试的 Core 负责。</summary>
public static class AuthProbe
{
    public static List<string>? ProbeCookieNamesOnDisk(long accountId)
        => CookieProbe.ProbeCookieNamesOnDisk(AccountWebHost.ProfileDir(accountId));

    public static bool IsLogged(IReadOnlyList<string>? names) => CookieProbe.IsLogged(names);

    public static async Task<List<(long Id, bool? Logged)>> ProbeAccountsAsync(IEnumerable<Account> accounts)
    {
        var result = new List<(long, bool?)>();
        foreach (var account in accounts)
        {
            var names = await Task.Run(() => ProbeCookieNamesOnDisk(account.Id));
            result.Add((account.Id, names == null ? null : IsLogged(names)));
        }
        return result;
    }
}
