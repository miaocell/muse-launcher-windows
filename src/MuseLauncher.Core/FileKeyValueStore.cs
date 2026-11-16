using System.Text.Encodings.Web;
using System.Text.Json;

namespace MuseLauncher.Core;

public interface IStoreLoadNotice
{
    string? TakeLoadNotice();
}

public sealed class StoreLoadException(string message, Exception? innerException = null) : IOException(message, innerException);

/// <summary>候选数据成功原子落盘后才提交内存，并保留独立文件级备份。</summary>
public sealed class FileKeyValueStore : IKeyValueStore, IStoreLoadNotice
{
    private const int MaximumFileBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private readonly string filePath;
    private readonly string backupPath;
    private readonly object gate = new();
    private Dictionary<string, object?> data = new();
    private string? loadNotice;

    public FileKeyValueStore(string filePath)
    {
        this.filePath = Path.GetFullPath(filePath);
        backupPath = this.filePath + ".bak";
        Load();
    }

    private static bool Exists(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.Directory) == 0; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        // Permission/I/O failures must propagate, not masquerade as a fresh installation.
    }

    private void Load()
    {
        if (!Exists(filePath) && !Exists(backupPath))
        {
            if (Directory.Exists(filePath) || Directory.Exists(backupPath))
                throw new StoreLoadException("账号存储路径不是文件，请检查目录或文件权限");
            return;
        }
        Exception? failure = null;
        try { data = ReadData(filePath); return; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            failure = ex;
        }
        // 留底成功后才允许恢复覆盖，读取失败时不要伪装为首次运行。
        try
        {
            if (File.Exists(filePath))
            {
                var archive = filePath + ".corrupt." + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N");
                File.Copy(filePath, archive, overwrite: false);
            }
            var restored = ReadData(backupPath);
            WriteAtomic(filePath, Serialize(restored));
            data = restored;
            loadNotice = "recovered";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            throw new StoreLoadException("账号文件读取失败且没有可用备份。原文件与账号目录已保留，请先恢复备份。", failure ?? ex);
        }
    }

    private static Dictionary<string, object?> ReadData(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumFileBytes) throw new IOException("账号文件过大");
        using var document = JsonDocument.Parse(stream);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("账号存储必须为 JSON 对象");
        if (!document.RootElement.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.String)
            throw new JsonException("账号存储缺少账号列表");
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var entry in document.RootElement.EnumerateObject())
        {
            if (values.ContainsKey(entry.Name)) throw new JsonException("账号存储存在重复字段");
            var value = entry.Value;
            values[entry.Name] = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number when value.TryGetInt64(out _) => (object)value.GetInt64(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => value.Clone(),
            };
        }
        return values;
    }

    private static byte[] Serialize(Dictionary<string, object?> values)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(values, WriteOptions);
        if (bytes.Length > MaximumFileBytes) throw new IOException("账号文件过大，未写入");
        return bytes;
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public string? GetString(string key, string? defaultValue)
    {
        lock (gate) return data.TryGetValue(key, out var value) && value is string text ? text : defaultValue;
    }

    public long GetLong(string key, long defaultValue)
    {
        lock (gate) return data.TryGetValue(key, out var value) && value is long number ? number : defaultValue;
    }

    public bool GetBoolean(string key, bool defaultValue)
    {
        lock (gate) return data.TryGetValue(key, out var value) && value is bool flag ? flag : defaultValue;
    }

    public void PutAll(IReadOnlyDictionary<string, object?> values)
    {
        lock (gate)
        {
            var candidate = new Dictionary<string, object?>(data, StringComparer.Ordinal);
            foreach (var entry in values) candidate[entry.Key] = entry.Value;
            candidate.TryAdd("list", "[]");
            var bytes = Serialize(candidate);
            // 仅备份真正成功提交过的内存状态，不把失败候选当作历史备份。
            if (data.Count > 0) WriteAtomic(backupPath, Serialize(data));
            WriteAtomic(filePath, bytes);
            data = candidate;
        }
    }

    public void PutString(string key, string? value) => PutAll(new Dictionary<string, object?> { [key] = value });
    public void PutLong(string key, long value) => PutAll(new Dictionary<string, object?> { [key] = value });
    public void PutBoolean(string key, bool value) => PutAll(new Dictionary<string, object?> { [key] = value });
    public string? TakeLoadNotice()
    {
        lock (gate)
        {
            var notice = loadNotice;
            loadNotice = null;
            return notice;
        }
    }
}
