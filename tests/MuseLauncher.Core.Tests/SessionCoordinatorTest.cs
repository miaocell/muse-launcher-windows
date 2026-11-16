using MuseLauncher.Core;
using Xunit;

namespace MuseLauncher.Core.Tests;

public class SessionCoordinatorTest
{
    private static readonly SessionLifecycleConfiguration Initial = new("initial");
    private static SessionCoordinator<SessionLifecycleResource, SessionLifecycleConfiguration> Coordinator(
        SessionLifecycleFactory factory, int capacity = 4) => new(capacity, factory.CreateAsync, factory.DisposeAsync);

    [Fact]
    public async Task CapacityCountsActualOwnedResources_CreatesOnlyAfterVictimCleanup()
    {
        var factory = new SessionLifecycleFactory();
        var coordinator = Coordinator(factory);
        var first = (await coordinator.ActivateAsync(1, Initial))!;
        for (long id = 2; id <= 4; id++) await coordinator.ActivateAsync(id, Initial);
        var cleanup = factory.BlockDisposal(first);
        var fifthPlan = factory.PlanCreation(5);
        var fifth = coordinator.ActivateAsync(5, Initial);
        var removedBeforeCreate = !coordinator.IsRegistered(1, first);
        var callsBeforeCleanup = factory.CallsFor(5);
        var ownedBeforeCleanup = factory.OwnedCount;
        cleanup.Release();
        await fifth;
        await coordinator.CloseAsync();

        Assert.True(removedBeforeCreate);
        Assert.Equal(0, callsBeforeCleanup);
        Assert.Equal(4, ownedBeforeCleanup);
        Assert.True(first.Disposed);
        Assert.Equal(4, factory.PeakOwned);
        Assert.Equal(0, factory.OwnedCount);
    }

    [Fact]
    public async Task SupersededRequestsAreSkipped_SerialFactoryDisposesLateResult()
    {
        var factory = new SessionLifecycleFactory();
        var coordinator = Coordinator(factory);
        var firstPlan = factory.PlanCreation(1, blocked: true);
        var first = coordinator.ActivateAsync(1, Initial);
        var old = await firstPlan.Started.Task;
        var skipped = coordinator.ActivateAsync(2, Initial);
        var last = coordinator.ActivateAsync(3, Initial);
        var callsBeforeRelease = factory.CallsFor(2) + factory.CallsFor(3);
        var peakCreating = factory.PeakCreating;
        firstPlan.Release();
        var firstResult = await first;
        var skippedResult = await skipped;
        var lastResult = await last;
        var snapshot = coordinator.Snapshot();
        await coordinator.CloseAsync();

        Assert.Equal(0, callsBeforeRelease);
        Assert.Equal(1, peakCreating);
        Assert.Null(firstResult);
        Assert.Null(skippedResult);
        Assert.NotNull(lastResult);
        Assert.True(old.Disposed);
        Assert.Equal(0, factory.CallsFor(2));
        Assert.Equal(new long[] { 3 }, snapshot.Keys);
        Assert.Equal(0, factory.OwnedCount);
    }

    [Fact]
    public async Task ReturningToCurrentWhileOtherCreationRuns_ProtectsCurrentFromEviction()
    {
        var factory = new SessionLifecycleFactory();
        var coordinator = Coordinator(factory);
        for (long id = 1; id <= 4; id++) await coordinator.ActivateAsync(id, Initial);
        var fourth = coordinator.Current!;
        var fifthPlan = factory.PlanCreation(5, blocked: true);
        var fifth = coordinator.ActivateAsync(5, Initial);
        await fifthPlan.Started.Task;
        var fourthResult = await coordinator.ActivateAsync(4, Initial);
        fifthPlan.Release();
        var staleResult = await fifth;
        var stillCurrent = coordinator.IsCurrent(4, fourth);
        var stillRegistered = coordinator.IsRegistered(4, fourth);
        var disposedBeforeClose = fourth.Disposed;
        await coordinator.CloseAsync();

        Assert.Same(fourth, fourthResult);
        Assert.Null(staleResult);
        Assert.True(stillCurrent);
        Assert.True(stillRegistered);
        Assert.False(disposedBeforeClose);
        Assert.Equal(4, factory.PeakOwned);
    }

    [Fact]
    public async Task SelectionChangesImmediately_CurrentNeverReturnsPreviousAccountWhileLoading()
    {
        var factory = new SessionLifecycleFactory();
        var coordinator = Coordinator(factory);
        var previous = (await coordinator.ActivateAsync(1, Initial))!;
        var plan = factory.PlanCreation(2, blocked: true);
        var activation = coordinator.ActivateAsync(2, Initial);
        var selected = coordinator.CurrentId;
        var currentBeforeCreation = coordinator.Current;
        var oldStillCurrent = coordinator.IsCurrent(1, previous);
        plan.Release();
        await activation;
        await coordinator.CloseAsync();

        Assert.Equal(2, selected);
        Assert.Null(currentBeforeCreation);
        Assert.False(oldStillCurrent);
    }

    [Fact]
    public async Task SameIdRequestsSerialize_NoSecondControllerBeforeFirstCleanup()
    {
        var factory = new SessionLifecycleFactory();
        var coordinator = Coordinator(factory);
        var firstPlan = factory.PlanCreation(1, blocked: true);
        var first = coordinator.ActivateAsync(1, Initial);
        var original = await firstPlan.Started.Task;
        var cleanup = factory.BlockDisposal(original);
        var secondPlan = factory.PlanCreation(1);
        var second = coordinator.ActivateAsync(1, Initial);
        firstPlan.Release();
        // With serialization the first late result must be disposed before the second factory.
        var winner = await Task.WhenAny(cleanup.Started.Task, second);
        var cleanupStartedBeforeSecond = ReferenceEquals(winner, cleanup.Started.Task);
        var callsBeforeCleanup = factory.CallsFor(1);
        cleanup.Release();
        var oldResult = await first;
        var newResult = await second;
        await coordinator.CloseAsync();

        Assert.True(cleanupStartedBeforeSecond);
        Assert.Equal(1, callsBeforeCleanup);
        Assert.Null(oldResult);
        Assert.NotSame(original, newResult);
        Assert.Equal(1, factory.PeakOwned);
        Assert.Equal(0, factory.OwnedCount);
    }

    [Fact]
    public async Task InvalidateDuringCreation_WaitsForCleanupAndNeverPublishesDeletedAccount()
    {
        var factory = new SessionLifecycleFactory();
        var coordinator = Coordinator(factory);
        var plan = factory.PlanCreation(1, blocked: true);
        var activation = coordinator.ActivateAsync(1, Initial);
        var resource = await plan.Started.Task;
        var invalidation = coordinator.InvalidateAsync(1);
        var returnedTooEarly = invalidation.IsCompleted;
        var selectedAfterInvalidation = coordinator.CurrentId;
        plan.Release();
        var result = await activation;
        await invalidation;
        var snapshot = coordinator.Snapshot();
        await coordinator.CloseAsync();

        Assert.False(returnedTooEarly);
        Assert.Null(selectedAfterInvalidation);
        Assert.Null(result);
        Assert.Empty(snapshot);
        Assert.True(resource.Disposed);
        Assert.Equal(0, factory.OwnedCount);
    }

    [Fact]
    public async Task ChangedConfigurationWaitsForOldFactoryAndDispose_BeforeCreatingNextGeneration()
    {
        var factory = new SessionLifecycleFactory();
        var coordinator = Coordinator(factory);
        var plan = factory.PlanCreation(1, blocked: true);
        var originalTask = coordinator.ActivateAsync(1, Initial);
        var original = await plan.Started.Task;
        var cleanup = factory.BlockDisposal(original);
        var changed = new SessionLifecycleConfiguration("changed");
        factory.PlanCreation(1);
        var replacement = coordinator.ActivateAsync(1, changed);
        plan.Release();
        var winner = await Task.WhenAny(cleanup.Started.Task, replacement);
        var waitedForCleanup = ReferenceEquals(winner, cleanup.Started.Task);
        var callsBeforeCleanup = factory.CallsFor(1);
        cleanup.Release();
        var oldResult = await originalTask;
        var newResult = await replacement;
        await coordinator.CloseAsync();

        Assert.True(waitedForCleanup);
        Assert.Equal(1, callsBeforeCleanup);
        Assert.Null(oldResult);
        Assert.Equal(changed, newResult!.Configuration);
        Assert.Equal(1, factory.PeakOwned);
    }

    [Fact]
    public async Task CloseDuringCreation_WaitsAndDisposesLateResult_ClearsAllState()
    {
        var factory = new SessionLifecycleFactory();
        var coordinator = Coordinator(factory);
        var plan = factory.PlanCreation(1, blocked: true);
        var activation = coordinator.ActivateAsync(1, Initial);
        var resource = await plan.Started.Task;
        var close = coordinator.CloseAsync();
        var closedTooEarly = close.IsCompleted;
        plan.Release();
        var result = await activation;
        await close;
        await coordinator.DisposeAsync();

        Assert.False(closedTooEarly);
        Assert.Null(result);
        Assert.True(resource.Disposed);
        Assert.Equal(1, resource.DisposalAttempts);
        Assert.Null(coordinator.CurrentId);
        Assert.Null(coordinator.Current);
        Assert.Empty(coordinator.Snapshot());
        Assert.Equal(0, factory.OwnedCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => coordinator.ActivateAsync(2, Initial));
    }

    [Fact]
    public async Task CancellationIgnoringFactory_StillDisposesAndDoesNotPublish()
    {
        var factory = new SessionLifecycleFactory();
        var coordinator = Coordinator(factory);
        using var cancellation = new CancellationTokenSource();
        var plan = factory.PlanCreation(1, blocked: true);
        var activation = coordinator.ActivateAsync(1, Initial, cancellation.Token);
        var resource = await plan.Started.Task;
        cancellation.Cancel();
        plan.Release();
        var result = await activation;
        var snapshot = coordinator.Snapshot();
        await coordinator.CloseAsync();

        Assert.Null(result);
        Assert.Empty(snapshot);
        Assert.True(resource.CreationToken.IsCancellationRequested);
        Assert.True(resource.Disposed);
        Assert.Equal(0, factory.OwnedCount);
    }

    [Fact]
    public async Task FactoryFailureCanRetryCurrentId_AndDoesNotReturnPreviousAccount()
    {
        var factory = new SessionLifecycleFactory();
        var coordinator = Coordinator(factory);
        var previous = (await coordinator.ActivateAsync(1, Initial))!;
        var plan = factory.PlanCreation(2, blocked: true);
        var failing = coordinator.ActivateAsync(2, Initial);
        plan.Fail(new IOException("create failed"));
        await Assert.ThrowsAsync<IOException>(() => failing);
        var currentAfterFailure = coordinator.Current;
        var retried = await coordinator.ActivateAsync(2, Initial);
        var retryIsCurrent = coordinator.IsCurrent(2, retried!);
        await coordinator.CloseAsync();

        Assert.Null(currentAfterFailure);
        Assert.NotSame(previous, retried);
        Assert.True(retryIsCurrent);
        Assert.Equal(2, factory.CallsFor(2));
        Assert.Equal(0, factory.OwnedCount);
    }

    [Fact]
    public async Task OldInstanceEventCannotInvalidateReplacement()
    {
        var factory = new SessionLifecycleFactory();
        var coordinator = Coordinator(factory);
        var previous = (await coordinator.ActivateAsync(1, Initial))!;
        await coordinator.InvalidateAsync(1);
        var replacement = (await coordinator.ActivateAsync(1, Initial))!;
        var removedByOldEvent = await coordinator.InvalidateAsync(1, previous);
        var replacementStillCurrent = coordinator.IsCurrent(1, replacement);
        var disposedBeforeClose = replacement.Disposed;
        await coordinator.CloseAsync();

        Assert.False(removedByOldEvent);
        Assert.True(replacementStillCurrent);
        Assert.False(disposedBeforeClose);
    }

    [Fact]
    public async Task IdentityAndSnapshotChecksDoNotRefreshLru()
    {
        var factory = new SessionLifecycleFactory();
        var coordinator = Coordinator(factory, capacity: 2);
        var first = (await coordinator.ActivateAsync(1, Initial))!;
        await coordinator.ActivateAsync(2, Initial);
        Assert.True(coordinator.IsRegistered(1, first));
        coordinator.Snapshot();
        await coordinator.ActivateAsync(3, Initial);
        var firstDisposed = first.Disposed;
        await coordinator.CloseAsync();

        Assert.True(firstDisposed);
    }

    [Fact]
    public async Task CloseAttemptsEveryResource_WhenOneDisposeFails_AndReportsFailure()
    {
        var factory = new SessionLifecycleFactory();
        var coordinator = Coordinator(factory);
        var first = (await coordinator.ActivateAsync(1, Initial))!;
        var second = (await coordinator.ActivateAsync(2, Initial))!;
        factory.FailNextDisposal(1);
        var error = await Record.ExceptionAsync(() => coordinator.CloseAsync());

        Assert.NotNull(error);
        Assert.Contains("cleanup failed", error.ToString());
        Assert.Equal(1, first.DisposalAttempts);
        Assert.Equal(1, second.DisposalAttempts);
        Assert.True(second.Disposed);
        Assert.Empty(coordinator.Snapshot());
        Assert.Null(coordinator.Current);
    }

    [Fact]
    public async Task FailedEvictionDoesNotStartReplacement_AndCloseRetriesOwnedResource()
    {
        var factory = new SessionLifecycleFactory();
        var coordinator = Coordinator(factory, capacity: 1);
        var first = (await coordinator.ActivateAsync(1, Initial))!;
        factory.FailNextDisposal(1);
        var error = await Record.ExceptionAsync(() => coordinator.ActivateAsync(2, Initial));
        var createdUnsafeReplacement = factory.CallsFor(2);
        await coordinator.CloseAsync();

        Assert.NotNull(error);
        Assert.Contains("cleanup failed", error.ToString());
        Assert.Equal(0, createdUnsafeReplacement);
        Assert.True(first.Disposed);
        Assert.Equal(2, first.DisposalAttempts);
        Assert.Equal(1, factory.PeakOwned);
        Assert.Equal(0, factory.OwnedCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveCapacityIsRejected(int capacity)
    {
        var factory = new SessionLifecycleFactory();
        Assert.Throws<ArgumentOutOfRangeException>(() => Coordinator(factory, capacity));
    }
}
