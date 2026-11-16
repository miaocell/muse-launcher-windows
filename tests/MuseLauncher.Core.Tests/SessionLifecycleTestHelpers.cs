namespace MuseLauncher.Core.Tests;

internal sealed record SessionLifecycleConfiguration(string Value);

internal sealed class SessionLifecycleResource(
    long id, SessionLifecycleConfiguration configuration, CancellationToken token)
{
    public long Id { get; } = id;
    public SessionLifecycleConfiguration Configuration { get; } = configuration;
    public CancellationToken CreationToken { get; } = token;
    public int DisposalAttempts { get; set; }
    public bool Disposed { get; set; }
}

/// <summary>Explicit gates, never sleeps: allocation is counted before the factory awaits.</summary>
internal sealed class SessionLifecycleFactory
{
    private readonly object sync = new();
    private readonly Dictionary<long, Queue<Creation>> creations = new();
    private readonly Dictionary<SessionLifecycleResource, Disposal> disposals = new();
    private readonly Dictionary<long, int> disposalFailures = new();
    private readonly HashSet<SessionLifecycleResource> owned = new();
    private readonly List<long> createCalls = new();
    private int creating;

    public int PeakOwned { get; private set; }
    public int PeakCreating { get; private set; }
    public int OwnedCount { get { lock (sync) return owned.Count; } }
    public IReadOnlyList<SessionLifecycleResource> Owned { get { lock (sync) return owned.ToArray(); } }
    public int CallsFor(long id) { lock (sync) return createCalls.Count(x => x == id); }

    public Creation PlanCreation(long id, bool blocked = false)
    {
        var plan = new Creation();
        if (!blocked) plan.Release();
        lock (sync)
        {
            if (!creations.TryGetValue(id, out var queue)) creations[id] = queue = new();
            queue.Enqueue(plan);
        }
        return plan;
    }

    public Disposal BlockDisposal(SessionLifecycleResource resource)
    {
        var plan = new Disposal();
        lock (sync) disposals.Add(resource, plan);
        return plan;
    }

    public void FailNextDisposal(long id)
    {
        lock (sync) disposalFailures[id] = disposalFailures.GetValueOrDefault(id) + 1;
    }

    public async Task<SessionLifecycleResource> CreateAsync(
        long id, SessionLifecycleConfiguration configuration, CancellationToken token)
    {
        Creation plan;
        SessionLifecycleResource resource;
        lock (sync)
        {
            plan = creations.TryGetValue(id, out var queue) && queue.Count > 0
                ? queue.Dequeue() : Creation.Released();
            resource = new(id, configuration, token);
            createCalls.Add(id);
            owned.Add(resource);
            PeakOwned = Math.Max(PeakOwned, owned.Count);
            PeakCreating = Math.Max(PeakCreating, ++creating);
        }
        plan.Started.TrySetResult(resource);
        try
        {
            // Deliberately ignores cancellation: the coordinator must await and clean up.
            await plan.Resume.Task;
            return resource;
        }
        catch
        {
            // A failing factory retains responsibility for partially constructed resources.
            lock (sync) owned.Remove(resource);
            resource.Disposed = true;
            throw;
        }
        finally
        {
            lock (sync) creating--;
        }
    }

    public async Task DisposeAsync(SessionLifecycleResource resource)
    {
        Disposal? plan;
        bool fail;
        lock (sync)
        {
            resource.DisposalAttempts++;
            disposals.TryGetValue(resource, out plan);
            fail = disposalFailures.GetValueOrDefault(resource.Id) > 0;
            if (fail) disposalFailures[resource.Id]--;
        }
        plan?.Started.TrySetResult(true);
        if (plan != null) await plan.Resume.Task;
        if (fail) throw new IOException($"browser-exit cleanup failed for {resource.Id}");
        lock (sync) owned.Remove(resource);
        resource.Disposed = true;
    }

    internal sealed class Creation
    {
        public TaskCompletionSource<SessionLifecycleResource> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => Resume.TrySetResult(true);
        public void Fail(Exception error) => Resume.TrySetException(error);
        public static Creation Released() { var plan = new Creation(); plan.Release(); return plan; }
    }

    internal sealed class Disposal
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => Resume.TrySetResult(true);
    }
}
