using System.Text.Json;
using MuseLauncher.Core;
using Xunit;

namespace MuseLauncher.Core.Tests;

public sealed class FileKeyValueStoreTest : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "MuseLauncherCoreTests", Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(root, "accounts.json");

    public FileKeyValueStoreTest() => Directory.CreateDirectory(root);

    private void WriteEnvelope(string list = "[]", object? nextId = null)
    {
        File.WriteAllText(FilePath, JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["list"] = list,
            ["nextId"] = nextId ?? 1L,
            ["currentId"] = -1L,
        }));
    }

    [Fact]
    public void OuterJsonCorruption_RecoversIndependentBackupAndNotices()
    {
        var kv = new FileKeyValueStore(FilePath);
        var store = new AccountsStore(kv);
        store.AddDefault();
        store.Rename(1, "committed-old");
        store.Rename(1, "committed-new");
        File.AppendAllText(FilePath, "BROKEN OUTER JSON");

        var recovered = new AccountsStore(new FileKeyValueStore(FilePath));
        Assert.Single(recovered.All());
        Assert.Equal("committed-old", recovered.All()[0].Name);
        Assert.Equal("recovered", recovered.TakeLoadNotice());
        Assert.NotEmpty(Directory.GetFiles(root, "accounts.json.corrupt.*"));
        Assert.Equal(2L, recovered.AddDefault().Id);
    }

    [Fact]
    public void OuterJsonCorruptionWithoutBackup_ThrowsInsteadOfFirstStartup()
    {
        WriteEnvelope("[{\"id\":1,\"name\":\"synthetic\"}]", 2L);
        File.AppendAllText(FilePath, "BROKEN");
        Assert.ThrowsAny<IOException>(() => new FileKeyValueStore(FilePath));
        Assert.Contains("BROKEN", File.ReadAllText(FilePath));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"list\":null}")]
    [InlineData("{\"list\":false}")]
    [InlineData("{\"list\":[]}")]
    [InlineData("[]")]
    public void ExistingInvalidEnvelope_IsNotAnEmptyFirstStartup(string raw)
    {
        File.WriteAllText(FilePath, raw);
        Assert.ThrowsAny<IOException>(() => new FileKeyValueStore(FilePath));
        Assert.Equal(raw, File.ReadAllText(FilePath));
    }

    [Theory]
    [InlineData(9007199254740993L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    [InlineData(42L)]
    public void GetLong_RetainsExactIntegerBits(long value)
    {
        WriteEnvelope(nextId: value);
        var kv = new FileKeyValueStore(FilePath);
        Assert.Equal(value, kv.GetLong("nextId", -77));
    }

    [Theory]
    [InlineData("1.9")]
    [InlineData("1.0")]
    [InlineData("9223372036854775808")]
    [InlineData("1e100")]
    public void GetLong_RejectsFractionalAndOutOfRangeNumbers(string number)
    {
        File.WriteAllText(FilePath, "{\"list\":\"[]\",\"nextId\":" + number + "}");
        Assert.Equal(-77L, new FileKeyValueStore(FilePath).GetLong("nextId", -77));
    }

    [Fact]
    public void WriteFailure_ThrowsAndDoesNotPublishCandidateDictionary()
    {
        WriteEnvelope();
        var kv = new FileKeyValueStore(FilePath);
        kv.PutString("label", "committed");
        File.Delete(FilePath);
        Directory.CreateDirectory(FilePath);

        Assert.ThrowsAny<IOException>(() => kv.PutString("label", "uncommitted"));
        Assert.Equal("committed", kv.GetString("label", null));
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}
