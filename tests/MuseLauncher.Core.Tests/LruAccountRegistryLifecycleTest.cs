using MuseLauncher.Core;
using Xunit;

namespace MuseLauncher.Core.Tests;

public class LruAccountRegistryLifecycleTest
{
    [Fact]
    public void OverCapacity_ReturnedResourcesAreDisposed_ActualOwnedSetStaysWithinBudget()
    {
        var registry = new LruAccountRegistry<SessionLifecycleResource>(4);
        var owned = new HashSet<SessionLifecycleResource>();
        for (long id = 1; id <= 5; id++)
        {
            var resource = new SessionLifecycleResource(id, new("initial"), default);
            owned.Add(resource);
            // 返回实际资源，不能再用已移除的 ID 回查。
            var evicted = registry.TouchAndEvictEntries(id, resource);
            foreach (var entry in evicted)
            {
                entry.Value.Disposed = true;
                owned.Remove(entry.Value);
            }
        }

        Assert.Equal(4, registry.Size);
        Assert.Equal(4, owned.Count);
    }

    [Fact]
    public void LegacyIdsAreAlreadyRemoved_ASecondRemoveCannotRecoverTheResource()
    {
        var registry = new LruAccountRegistry<object>(1);
        var old = new object();
        registry.Put(1, old);
        var evictedId = Assert.Single(registry.TouchAndEvict(2, new object()));

        Assert.Equal(1, evictedId);
        Assert.Null(registry.Remove(evictedId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveCapacity_IsRejected(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LruAccountRegistry<object>(capacity));
    }
}
