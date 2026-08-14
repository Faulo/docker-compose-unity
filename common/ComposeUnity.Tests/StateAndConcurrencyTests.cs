namespace ComposeUnity.Tests;

public sealed class StateAndConcurrencyTests {
    [Fact]
    public void PersistsAndDrainsSidecarState() {
        using var directory = new TemporaryDirectory();
        var store = new StateStore(directory.path);
        var invocation = new InvocationRecord {
            id = "abc",
            command = "tests",
            workingDirectory = "/workspace",
            startedAtUtc = DateTimeOffset.UtcNow,
            timeoutSeconds = 10
        };
        var ready = new ReadyRecord { supervisor = ProcessIdentity.Current(), mcpEnabled = true, mcpReady = true };

        store.EnsureDirectories();
        Assert.True(store.CanWrite());
        store.WriteActive(invocation);
        store.WriteReady(ready);
        store.WriteEvent(LifecycleEvent.Start(invocation));

        Assert.Equal("abc", Assert.Single(store.ReadActive()).id);
        Assert.True(store.ReadReady()?.mcpReady);
        var events = new List<LifecycleEvent>();
        store.DrainEvents(events.Add);
        Assert.Equal("START", Assert.Single(events).kind);
        store.DrainEvents(events.Add);
        Assert.Single(events);

        store.RemoveActive("abc");
        store.RemoveReady();
        Assert.Empty(store.ReadActive());
        Assert.Null(store.ReadReady());
    }

    [Fact]
    public async Task GrantsFifoLockInRequestOrder() {
        var fifo = new AsyncFifoLock();
        var order = new List<int>();
        await using var first = await fifo.AcquireAsync(CancellationToken.None);
        var second = enterAsync(2);
        var third = enterAsync(3);

        await first.DisposeAsync();
        await Task.WhenAll(second, third);

        Assert.Equal([2, 3], order);

        async Task enterAsync(int value) {
            await using var lease = await fifo.AcquireAsync(CancellationToken.None);
            order.Add(value);
        }
    }

    [Fact]
    public async Task SkipsCancelledFifoWaiter() {
        var fifo = new AsyncFifoLock();
        await using var first = await fifo.AcquireAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var cancelled = fifo.AcquireAsync(cancellation.Token);
        var next = fifo.AcquireAsync(CancellationToken.None);
        cancellation.Cancel();

        await first.DisposeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await cancelled);
        await using var nextLease = await next;
    }

    [Theory]
    [InlineData("/project/", "/project")]
    [InlineData("/", "/")]
    [InlineData("C:\\project\\", "C:\\project")]
    public void NormalizesDaemonPaths(string value, string expected) =>
        Assert.Equal(expected, UnityMcpController.NormalizeDaemonPath(value));

    [Fact]
    public void NormalizesWindowsProjectIdentityAcrossControllerPlatforms() {
        Assert.Equal("C:\\PROJECT", UnityMcpController.ProjectIdentityPath("c:\\project", false));
        Assert.Equal("/PROJECT", UnityMcpController.ProjectIdentityPath("/project", true));
        Assert.Equal("/project", UnityMcpController.ProjectIdentityPath("/project", false));
    }

    sealed class TemporaryDirectory : IDisposable {
        internal TemporaryDirectory() {
            path = Path.Combine(Path.GetTempPath(), "compose-unity-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
        }

        internal string path { get; }

        public void Dispose() => Directory.Delete(path, true);
    }
}
