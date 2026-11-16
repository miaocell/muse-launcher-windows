namespace MuseLauncher.Core;

/// <summary>
/// 按账号保存资源并维护 LRU 顺序。注册表只转移资源所有权，不负责 Dispose；
/// 重资源调用方应使用返回实例的 API，而不是根据已移除的 id 再查找资源。
/// 本类本身不保证线程安全。
/// </summary>
public class LruAccountRegistry<T> where T : class
{
    private readonly int maxAlive;
    private readonly Dictionary<long, T> map = new();
    private readonly LinkedList<long> order = new(); // First = 最久未用
    private readonly Dictionary<long, LinkedListNode<long>> nodes = new();

    public LruAccountRegistry(int maxAlive)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAlive, 1);
        this.maxAlive = maxAlive;
    }

    /// <summary>取得实例并刷新最近使用顺序。</summary>
    public T? Get(long id)
    {
        var value = Peek(id);
        if (value != null) MoveToNewest(id);
        return value;
    }

    /// <summary>只检查登记，不刷新 LRU；适用于事件来源的身份校验。</summary>
    public T? Peek(long id) => map.GetValueOrDefault(id);

    /// <summary>登记/刷新资源。替换已有实例之前，调用方负责取走并销毁旧实例。</summary>
    public void Put(long id, T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (map.ContainsKey(id))
        {
            map[id] = value;
            MoveToNewest(id);
        }
        else
        {
            map[id] = value;
            var node = new LinkedListNode<long>(id);
            order.AddLast(node);
            nodes[id] = node;
        }
    }

    public T? Remove(long id)
    {
        if (!map.Remove(id, out var value)) return null;
        if (nodes.Remove(id, out var node)) order.Remove(node);
        return value;
    }

    /// <summary>移除最旧的非保护资源，直接把实例所有权交给调用方。</summary>
    public (long Id, T Value)? RemoveOldest(IReadOnlySet<long>? excludedIds = null)
    {
        for (var node = order.First; node != null; node = node.Next)
        {
            if (excludedIds?.Contains(node.Value) == true) continue;
            var id = node.Value;
            return (id, Remove(id)!);
        }
        return null;
    }

    public bool Contains(long id) => map.ContainsKey(id);
    public int Size => map.Count;

    /// <summary>独立副本；遍历和身份检查不改变最近使用顺序。</summary>
    public IReadOnlyDictionary<long, T> Snapshot() => new Dictionary<long, T>(map);

    /// <summary>
    /// 兼容旧调用方的决策 API：返回的 id 已被移除，不能再通过 Remove 获取实例。
    /// WebView 等重资源必须使用 TouchAndEvictEntries 或 RemoveOldest。
    /// </summary>
    public List<long> TouchAndEvict(long id, T value) =>
        TouchAndEvictEntries(id, value).Select(entry => entry.Id).ToList();

    /// <summary>登记并驱逐超容量资源；返回已移除的实际实例，供调用方逐个销毁。</summary>
    public List<(long Id, T Value)> TouchAndEvictEntries(long id, T value)
    {
        Put(id, value);
        var evicted = new List<(long Id, T Value)>();
        while (map.Count > maxAlive)
        {
            var entry = RemoveOldest();
            if (entry == null) break;
            evicted.Add(entry.Value);
        }
        return evicted;
    }

    private void MoveToNewest(long id)
    {
        if (!nodes.TryGetValue(id, out var node)) return;
        order.Remove(node);
        order.AddLast(node);
    }
}
