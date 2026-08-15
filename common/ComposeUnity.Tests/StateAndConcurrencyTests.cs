using System.Text.Json.Nodes;

namespace ComposeUnity.Tests;

public sealed class StateAndConcurrencyTests {
    [Test]
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
        Assert.That(store.CanWrite(), Is.True);
        store.WriteActive(invocation);
        store.WriteReady(ready);
        store.WriteEvent(LifecycleEvent.Start(invocation));

        var active = store.ReadActive();
        Assert.That(active, Has.Count.EqualTo(1));
        Assert.That(active.Single().id, Is.EqualTo("abc"));
        Assert.That(store.ReadReady()?.mcpReady, Is.True);
        var events = new List<LifecycleEvent>();
        store.DrainEvents(events.Add);
        Assert.That(events, Has.Count.EqualTo(1));
        Assert.That(events.Single().kind, Is.EqualTo("START"));
        store.DrainEvents(events.Add);
        Assert.That(events, Has.Count.EqualTo(1));

        store.RemoveActive("abc");
        store.RemoveReady();
        Assert.That(store.ReadActive(), Is.Empty);
        Assert.That(store.ReadReady(), Is.Null);
    }

    [Test]
    public async Task GrantsFifoLockInRequestOrder() {
        var fifo = new AsyncFifoLock();
        var order = new List<int>();
        int waits = 0;
        await using var first = await fifo.AcquireAsync(CancellationToken.None);
        var second = enterAsync(2);
        var third = enterAsync(3);

        await first.DisposeAsync();
        await Task.WhenAll(second, third);

        using (Assert.EnterMultipleScope()) {
            Assert.That(order, Is.EqualTo(new[] { 2, 3 }));
            Assert.That(waits, Is.EqualTo(2));
        }

        async Task enterAsync(int value) {
            await using var lease = await fifo.AcquireAsync(CancellationToken.None, () => Interlocked.Increment(ref waits));
            order.Add(value);
        }
    }

    [Test]
    public async Task SkipsCancelledFifoWaiter() {
        var fifo = new AsyncFifoLock();
        await using var first = await fifo.AcquireAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var cancelled = fifo.AcquireAsync(cancellation.Token);
        var next = fifo.AcquireAsync(CancellationToken.None);
        cancellation.Cancel();

        await first.DisposeAsync();

        Assert.CatchAsync<OperationCanceledException>(async () => await cancelled);
        await using var nextLease = await next;
    }

    [TestCase("/project/", "/project")]
    [TestCase("/", "/")]
    [TestCase("C:\\project\\", "C:\\project")]
    public void NormalizesDaemonPaths(string value, string expected) =>
        Assert.That(UnityMcpController.NormalizeDaemonPath(value), Is.EqualTo(expected));

    [Test]
    public void NormalizesWindowsProjectIdentityAcrossControllerPlatforms() {
        Assert.That(UnityMcpController.ProjectIdentityPath("c:\\project", false), Is.EqualTo("C:\\PROJECT"));
        Assert.That(UnityMcpController.ProjectIdentityPath("/project", true), Is.EqualTo("/PROJECT"));
        Assert.That(UnityMcpController.ProjectIdentityPath("/project", false), Is.EqualTo("/project"));
    }

    [TestCase(@"C:\Users\Faulo\Desktop\Unity\Slothsoft.CritterGrove",
        "/run/desktop/mnt/host/c/Users/Faulo/Desktop/Unity/Slothsoft.CritterGrove")]
    [TestCase("D:/Projects/Game With Spaces", "/run/desktop/mnt/host/d/Projects/Game With Spaces")]
    [TestCase(@"E:\", "/run/desktop/mnt/host/e")]
    public void ResolvesWindowsPathsForDockerDesktopLinux(string value, string expected) =>
        Assert.That(UnityMcpController.ResolveDaemonProjectRoot(value, false, true), Is.EqualTo(expected));

    [Test]
    public void PreservesPathsWithoutDockerDesktopLinuxTranslation() {
        const string windowsPath = @"C:\Projects\Game";
        const string daemonPath = "/run/desktop/mnt/host/c/Projects/Game";
        using (Assert.EnterMultipleScope()) {
            Assert.That(UnityMcpController.ResolveDaemonProjectRoot(daemonPath, false, true), Is.EqualTo(daemonPath));
            Assert.That(UnityMcpController.ResolveDaemonProjectRoot(windowsPath, true, true), Is.EqualTo(windowsPath));
            Assert.That(UnityMcpController.ResolveDaemonProjectRoot(windowsPath, false, false), Is.EqualTo(windowsPath));
        }
    }

    [TestCase("Docker Desktop 4.86.0 (236216)", true)]
    [TestCase("docker desktop", true)]
    [TestCase("Docker Engine - Community", false)]
    [TestCase("Docker Desktop-Compatible", false)]
    public void IdentifiesDockerDesktopFromDaemonVersion(string platformName, bool expected) {
        var version = new JsonObject {
            ["Platform"] = new JsonObject { ["Name"] = platformName }
        };
        Assert.That(UnityMcpController.IsDockerDesktop(version), Is.EqualTo(expected));
    }

    [Test]
    public void CanonicalizesWorkerConfigurationFingerprints() {
        var first = JsonNode.Parse("""{"nested":{"b":2,"a":1},"enabled":true}""")!;
        var reordered = JsonNode.Parse("""{"enabled":true,"nested":{"a":1,"b":2}}""")!;
        var changed = JsonNode.Parse("""{"enabled":true,"nested":{"a":1,"b":3}}""")!;

        Assert.Multiple(() => {
            Assert.That(UnityMcpController.ConfigurationFingerprint(first),
                Is.EqualTo(UnityMcpController.ConfigurationFingerprint(reordered)));
            Assert.That(UnityMcpController.ConfigurationFingerprint(first),
                Is.Not.EqualTo(UnityMcpController.ConfigurationFingerprint(changed)));
        });
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
