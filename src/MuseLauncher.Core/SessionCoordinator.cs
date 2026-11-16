namespace MuseLauncher.Core;

/// <summary>
/// Owns account sessions, including in-flight creation and disposal. Call from the UI
/// context when the callbacks require it: callbacks are awaited without Task.Run or
/// ConfigureAwait(false). Callbacks must not await another coordinator operation.
/// A throwing factory is responsible for cleaning up its partially constructed resource.
/// </summary>
public sealed class SessionCoordinator<T, TConfig> : IAsyncDisposable where T : class
{
    private readonly object sync = new();
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly int capacity;
    private readonly LruAccountRegistry<T> resources;
    private readonly Func<long, TConfig, CancellationToken, Task<T>> create;
    private readonly Func<T, Task> dispose;
    private readonly IEqualityComparer<TConfig> configurationComparer;
    private readonly Func<TConfig, TConfig> snapshotConfiguration;
    private readonly Dictionary<long, TConfig> configurations = new();
    private readonly Dictionary<long, long> versions = new();
    private readonly Dictionary<T, Ownership> owned = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<T, Exception> cleanupFailures = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Activation> pending = new();
    private readonly List<Exception> cancellationFailures = new();
    private long generation;
    private long? currentId;
    private T? current;
    private bool closed;
    private Task? closeTask;

    public SessionCoordinator(int capacity,
        Func<long, TConfig, CancellationToken, Task<T>> createAsync,
        Func<T, Task> disposeAsync,
        IEqualityComparer<TConfig>? configurationComparer = null,
        Func<TConfig, TConfig>? snapshotConfiguration = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentNullException.ThrowIfNull(createAsync);
        ArgumentNullException.ThrowIfNull(disposeAsync);
        this.capacity = capacity;
        resources = new(capacity);
        create = createAsync;
        dispose = disposeAsync;
        this.configurationComparer = configurationComparer ?? EqualityComparer<TConfig>.Default;
        this.snapshotConfiguration = snapshotConfiguration ?? (configuration => configuration);
    }

    public long? CurrentId { get { lock (sync) return currentId; } }
    public T? Current { get { lock (sync) return current; } }

    /// <summary>Registered, fully created sessions only; does not refresh recency.</summary>
    public IReadOnlyDictionary<long, T> Snapshot()
    {
        lock (sync) return resources.Snapshot();
    }

    public bool IsRegistered(long id, T instance)
    {
        lock (sync) return ReferenceEquals(resources.Peek(id), instance);
    }

    public bool IsCurrent(long id, T instance)
    {
        lock (sync) return currentId == id && ReferenceEquals(current, instance)
            && ReferenceEquals(resources.Peek(id), instance);
    }

    /// <summary>
    /// Selection changes synchronously, before any await. Only the latest request can
    /// publish; superseded/cancelled requests return null after cleaning up. A current
    /// factory failure is propagated and the same id can be activated again to retry.
    /// Configurations must be immutable, or snapshotConfiguration must make a copy.
    /// </summary>
    public Task<T?> ActivateAsync(long id, TConfig configuration, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(id, 1);
        var snapshot = snapshotConfiguration(configuration);
        Activation[] superseded;
        Activation? activation = null;
        T? cached;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            var version = versions.GetValueOrDefault(id);
            if (configurations.TryGetValue(id, out var previous)
                && !configurationComparer.Equals(previous, snapshot))
            {
                versions[id] = version = version + 1;
                DetachNoLock(id);
            }
            configurations[id] = snapshot;
            versions[id] = version;
            generation++;
            currentId = id;
            current = null; // Never expose account A while account B is being created.
            superseded = pending.ToArray();
            cached = cancellationToken.IsCancellationRequested ? null : resources.Get(id);
            if (cached != null) current = cached;
            else if (!cancellationToken.IsCancellationRequested)
            {
                activation = new(id, snapshot, version, generation, cancellationToken);
                pending.Add(activation);
            }
        }
        Cancel(superseded);
        return activation == null ? Task.FromResult(cached) : ActivateCoreAsync(activation);
    }

    /// <summary>
    /// Invalidates synchronously, then waits for old creation/disposal. A subsequent
    /// activation is a different id-version and cannot be removed by this operation.
    /// </summary>
    public Task InvalidateAsync(long id)
    {
        long version;
        Activation[] invalidated;
        lock (sync)
        {
            if (closed) return closeTask ?? Task.CompletedTask;
            (version, invalidated) = InvalidateNoLock(id);
        }
        Cancel(invalidated);
        return InvalidateCoreAsync(id, version);
    }

    /// <summary>Use for host events: an old instance cannot invalidate its replacement.</summary>
    public Task<bool> InvalidateAsync(long id, T expectedInstance)
    {
        long version;
        Activation[] invalidated;
        lock (sync)
        {
            if (closed || !ReferenceEquals(resources.Peek(id), expectedInstance)) return Task.FromResult(false);
            (version, invalidated) = InvalidateNoLock(id);
        }
        Cancel(invalidated);
        return InvalidateExpectedCoreAsync(id, version);
    }

    /// <summary>
    /// Stops admission immediately, awaits in-flight creation and attempts every owned
    /// disposal even if one fails. Repeated calls return the same completion/failure.
    /// </summary>
    public Task CloseAsync()
    {
        TaskCompletionSource<bool> completion;
        Activation[] outstanding;
        lock (sync)
        {
            if (closeTask != null) return closeTask;
            closed = true;
            generation++;
            currentId = null;
            current = null;
            foreach (var id in resources.Snapshot().Keys) resources.Remove(id);
            outstanding = pending.ToArray();
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            closeTask = completion.Task;
        }
        Cancel(outstanding);
        // CloseCore catches and forwards every exception to completion; no unobserved task.
        _ = CloseCoreAsync(outstanding, completion);
        return completion.Task;
    }

    public ValueTask DisposeAsync() => new(CloseAsync());

    private async Task<T?> ActivateCoreAsync(Activation activation)
    {
        try
        {
            await lifecycleGate.WaitAsync();
            try
            {
                lock (sync)
                {
                    if (!IsValidNoLock(activation)) return null;
                    var cached = resources.Get(activation.Id);
                    if (cached != null) { current = cached; return cached; }
                    ThrowIfCleanupFailedNoLock();
                }

                // Serial lifecycle work also prevents two generations using one profile
                // concurrently. Dispose completion must mean browser resources are gone.
                await DisposeDetachedAsync(activation.Id);
                while (true)
                {
                    T? victim;
                    lock (sync)
                    {
                        if (!IsValidNoLock(activation)) return null;
                        ThrowIfCleanupFailedNoLock();
                        if (owned.Count < capacity) break;
                        victim = owned.Keys.FirstOrDefault(instance =>
                            !ReferenceEquals(resources.Peek(owned[instance].Id), instance));
                        if (victim == null)
                        {
                            var excluded = currentId.HasValue ? new HashSet<long> { currentId.Value } : null;
                            victim = resources.RemoveOldest(excluded)?.Value;
                        }
                        if (victim == null)
                            throw new InvalidOperationException("No non-current session is available for eviction.");
                    }
                    // Remain within budget during disposal too; never create first.
                    await DisposeOwnedAsync(victim);
                }

                lock (sync)
                {
                    if (!IsValidNoLock(activation)) return null;
                    ThrowIfCleanupFailedNoLock();
                }
                T instance;
                try
                {
                    // The gate reserves the only creation slot; owned.Count <= capacity-1.
                    instance = await create(activation.Id, activation.Configuration, activation.Cancellation.Token);
                    if (instance == null) throw new InvalidOperationException("Session factory returned null.");
                }
                catch
                {
                    lock (sync) { if (!IsValidNoLock(activation)) return null; }
                    throw;
                }

                lock (sync)
                {
                    if (!owned.TryAdd(instance, new(activation.Id, activation.Version)))
                        throw new InvalidOperationException("Session factory returned an already owned instance.");
                    if (IsValidNoLock(activation))
                    {
                        resources.Put(activation.Id, instance);
                        current = instance;
                        return instance;
                    }
                }
                await DisposeOwnedAsync(instance);
                return null;
            }
            finally { lifecycleGate.Release(); }
        }
        finally
        {
            lock (sync) pending.Remove(activation);
            activation.Cancellation.Dispose();
            activation.Completion.TrySetResult(true);
        }
    }

    private (long Version, Activation[] Invalidated) InvalidateNoLock(long id)
    {
        var version = versions.GetValueOrDefault(id) + 1;
        versions[id] = version;
        DetachNoLock(id);
        if (currentId == id)
        {
            generation++;
            currentId = null;
            current = null;
        }
        return (version, pending.Where(activation => activation.Id == id).ToArray());
    }

    private void DetachNoLock(long id)
    {
        var removed = resources.Remove(id);
        if (removed != null && ReferenceEquals(current, removed)) current = null;
    }

    private bool IsValidNoLock(Activation activation) => !closed
        && !activation.Cancellation.IsCancellationRequested
        && activation.Generation == generation && currentId == activation.Id
        && versions.GetValueOrDefault(activation.Id) == activation.Version
        && configurations.TryGetValue(activation.Id, out var configuration)
        && configurationComparer.Equals(configuration, activation.Configuration);

    private async Task DisposeDetachedAsync(long id)
    {
        T[] detached;
        lock (sync) detached = owned.Where(entry => entry.Value.Id == id
            && !ReferenceEquals(resources.Peek(id), entry.Key)).Select(entry => entry.Key).ToArray();
        foreach (var instance in detached) await DisposeOwnedAsync(instance);
    }

    private async Task InvalidateCoreAsync(long id, long version)
    {
        await lifecycleGate.WaitAsync();
        try
        {
            T[] invalidated;
            lock (sync) invalidated = owned.Where(entry => entry.Value.Id == id
                && entry.Value.Version < version).Select(entry => entry.Key).ToArray();
            var failures = new List<Exception>();
            foreach (var instance in invalidated)
            {
                try { await DisposeOwnedAsync(instance); }
                catch (Exception error) { failures.Add(error); }
            }
            if (failures.Count != 0) throw new AggregateException("Session invalidation cleanup failed.", failures);
        }
        finally { lifecycleGate.Release(); }
    }

    private async Task<bool> InvalidateExpectedCoreAsync(long id, long version)
    {
        await InvalidateCoreAsync(id, version);
        return true;
    }

    private async Task DisposeOwnedAsync(T instance)
    {
        Ownership ownership;
        lock (sync)
        {
            if (!owned.TryGetValue(instance, out ownership)) return;
        }
        try { await dispose(instance); }
        catch (Exception error)
        {
            var failure = new InvalidOperationException($"Session {ownership.Id} resource cleanup failed.", error);
            lock (sync) cleanupFailures[instance] = failure;
            throw failure;
        }
        lock (sync)
        {
            owned.Remove(instance);
            cleanupFailures.Remove(instance);
        }
    }

    private void ThrowIfCleanupFailedNoLock()
    {
        if (cleanupFailures.Count != 0)
            throw new InvalidOperationException("Previous resource cleanup failed; invalidate or close before creating another session.",
                new AggregateException(cleanupFailures.Values));
    }

    private async Task CloseCoreAsync(Activation[] outstanding, TaskCompletionSource<bool> completion)
    {
        var failures = new List<Exception>();
        try
        {
            await lifecycleGate.WaitAsync();
            try
            {
                T[] instances;
                lock (sync) instances = owned.Keys.ToArray();
                foreach (var instance in instances)
                {
                    try { await DisposeOwnedAsync(instance); }
                    catch (Exception error) { failures.Add(error); }
                }
            }
            finally { lifecycleGate.Release(); }
            await Task.WhenAll(outstanding.Select(activation => activation.Completion.Task));
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            lock (sync)
            {
                failures.AddRange(cancellationFailures);
                cancellationFailures.Clear();
                configurations.Clear();
                versions.Clear();
                owned.Clear();
                cleanupFailures.Clear();
            }
        }
        if (failures.Count == 0) completion.TrySetResult(true);
        else completion.TrySetException(new AggregateException("Session close cleanup failed.", failures));
    }

    private void Cancel(IEnumerable<Activation> activations)
    {
        foreach (var activation in activations)
        {
            try { activation.Cancellation.Cancel(); }
            catch (ObjectDisposedException) { /* Operation already completed. */ }
            catch (Exception error) { lock (sync) cancellationFailures.Add(error); }
        }
    }

    private readonly record struct Ownership(long Id, long Version);

    private sealed class Activation(long id, TConfig configuration, long version, long generation, CancellationToken token)
    {
        public long Id { get; } = id;
        public TConfig Configuration { get; } = configuration;
        public long Version { get; } = version;
        public long Generation { get; } = generation;
        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(token);
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
