using System.Text.Json;
using Xunit;

namespace MuseLauncher.Core.Tests;

public class RepairIntegrationRegressionTest
{
    [Fact]
    public void ReservedProfileIdsAreNeverGivenToNewAccounts()
    {
        using var directory = new TemporaryDirectory();
        var store = new AccountsStore(new FileKeyValueStore(directory.FilePath), id => id is 1 or 2 or 4);
        Assert.Equal(3, store.AddDefault().Id);
        Assert.Equal(5, store.AddDefault().Id);
    }

    [Fact]
    public void AccountSnapshotsCannotMutatePublishedState()
    {
        using var directory = new TemporaryDirectory();
        var store = new AccountsStore(new FileKeyValueStore(directory.FilePath));
        var added = store.AddDefault();
        added.Name = "not committed";
        added.Tags.Add("not committed");
        store.Save();
        Assert.Equal("Muse 1", store.FindById(added.Id)!.Name);
        Assert.Empty(store.FindById(added.Id)!.Tags);
        var draft = store.FindById(added.Id)!;
        draft.Name = "committed";
        store.Update(draft);
        Assert.Equal("committed", new AccountsStore(new FileKeyValueStore(directory.FilePath)).FindById(added.Id)!.Name);
    }

    [Fact]
    public void ProxyAndBypassInheritanceAreSavedInOneAtomicUpdate()
    {
        using var directory = new TemporaryDirectory();
        var store = new AccountsStore(new FileKeyValueStore(directory.FilePath));
        var added = store.AddDefault("http://user:pass@proxy.example:8000", inheritForceProxy: true);
        var restored = new AccountsStore(new FileKeyValueStore(directory.FilePath)).FindById(added.Id)!;
        Assert.Equal(added.Proxy, restored.Proxy);
        Assert.True(restored.ForceProxy);
    }

    [Fact]
    public void FailedUpdateDoesNotChangePublishedProxyCredentials()
    {
        using var directory = new TemporaryDirectory();
        var store = new AccountsStore(new FileKeyValueStore(directory.FilePath));
        var account = store.AddDefault("http://old:password@proxy.example:8000");
        var draft = account.Clone();
        draft.Proxy = "http://new:changed@proxy.example:8000";
        File.Delete(directory.FilePath);
        Directory.CreateDirectory(directory.FilePath);
        Assert.ThrowsAny<IOException>(() => store.Update(draft));
        Assert.Equal(account.Proxy, store.FindById(account.Id)!.Proxy);
    }

    [Fact]
    public void RuntimeLoginStatusDoesNotWriteOrGetOverwrittenByOldDraft()
    {
        using var directory = new TemporaryDirectory();
        var store = new AccountsStore(new FileKeyValueStore(directory.FilePath));
        var account = store.AddDefault();
        var before = File.ReadAllText(directory.FilePath);
        store.SetLogged(account.Id, true);
        Assert.Equal(before, File.ReadAllText(directory.FilePath));
        account.Name = "draft update";
        store.Update(account);
        Assert.True(store.FindById(account.Id)!.Logged);
        Assert.False(new AccountsStore(new FileKeyValueStore(directory.FilePath)).FindById(account.Id)!.Logged);
    }

    [Fact]
    public void FailedReloadKeepsPreviouslyLoadedAccounts()
    {
        using var directory = new TemporaryDirectory();
        var keyValues = new FileKeyValueStore(directory.FilePath);
        var store = new AccountsStore(keyValues);
        var account = store.AddDefault();
        keyValues.PutAll(new Dictionary<string, object?> { ["list"] = "broken", ["list_prev"] = "also broken" });
        Assert.Throws<StoreLoadException>(() => store.Reload());
        Assert.Equal(account.Id, Assert.Single(store.All()).Id);
    }

    [Fact]
    public void MissingValueDoesNotConsumeShortOrLongOption()
    {
        var first = LaunchCommands.Parse(new[] { "--start_account", "--show_drawer" });
        var second = LaunchCommands.Parse(new[] { "--open_config", "-show_drawer" });
        Assert.Equal(-1, first.StartAccount);
        Assert.True(first.ShowDrawer);
        Assert.True(second.HasOpenConfig);
        Assert.True(second.ShowDrawer);
    }

    [Fact]
    public void NewlineInArgumentRemainsDataInsteadOfDeleteCommand()
    {
        var arguments = new[] { "--search_query=line1\n--del_account=1" };
        var restored = LaunchArgumentsCodec.Decode(LaunchArgumentsCodec.Encode(arguments));
        var command = LaunchCommands.Parse(restored);
        Assert.False(command.HasDelAccount);
        Assert.Equal("line1\n--del_account=1", command.SearchQuery);
    }

    [Fact]
    public void CorruptBackupAndMainNeverResetTheAccountCounter()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(directory.FilePath, "broken-main");
        File.WriteAllText(directory.FilePath + ".bak", "broken-backup");
        Assert.Throws<StoreLoadException>(() => new FileKeyValueStore(directory.FilePath));
        Assert.Equal("broken-main", File.ReadAllText(directory.FilePath));
        Assert.Equal("broken-backup", File.ReadAllText(directory.FilePath + ".bak"));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "MuseRepairRegression-" + Guid.NewGuid().ToString("N"));
        public string FilePath => Path.Combine(root, "accounts.json");
        public TemporaryDirectory() => Directory.CreateDirectory(root);
        public void Dispose() => Directory.Delete(root, recursive: true);
    }
}
