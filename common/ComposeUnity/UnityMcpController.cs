using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;

namespace ComposeUnity;

sealed class UnityMcpController : IAsyncDisposable {
    const string LABEL_PREFIX = "net.slothsoft.compose-unity";
    static readonly TimeSpan WorkerStopTimeout = TimeSpan.FromSeconds(10);

    static readonly string[] ForwardedEnvironmentNames = [
        "UNITY_NO_GRAPHICS",
        "UNITY_ACCELERATOR_ENDPOINT",
        "UNITY_ACCELERATOR_PARAMS",
        "UNITY_LOGGING",
        "UNITY_EMPTY_MANIFEST",
        "UNITY_CREDENTIALS_USR",
        "UNITY_CREDENTIALS_PSW",
        "EMAIL_CREDENTIALS_USR",
        "EMAIL_CREDENTIALS_PSW",
        "COMPOSE_UNITY_CALL_TIMEOUT"
    ];

    static readonly string[] InheritedHostConfigurationNames = [
        "Memory",
        "MemorySwap",
        "MemoryReservation",
        "NanoCpus",
        "CpuShares",
        "CpuPeriod",
        "CpuQuota",
        "CpusetCpus",
        "CpusetMems",
        "PidsLimit",
        "OomKillDisable",
        "ShmSize",
        "Ulimits",
        "BlkioWeight",
        "BlkioWeightDevice",
        "BlkioDeviceReadBps",
        "BlkioDeviceWriteBps",
        "BlkioDeviceReadIOps",
        "BlkioDeviceWriteIOps",
        "DeviceRequests",
        "Devices",
        "Isolation"
    ];

    readonly ConcurrentDictionary<string, byte> activeWorkers = new(StringComparer.Ordinal);
    readonly string controllerId;

    readonly DockerEngineClient docker;
    readonly string imageHash;
    readonly string imageId;
    readonly ConcurrentDictionary<string, AsyncFifoLock> lanes = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, Lazy<Task<ValidatedProject>>> projects = new(StringComparer.Ordinal);
    readonly JsonObject self;
    readonly CancellationToken stoppingToken;
    readonly bool windowsContainers;

    UnityMcpController(
        DockerEngineClient docker,
        JsonObject self,
        string controllerId,
        string imageId,
        bool windowsContainers,
        CancellationToken stoppingToken) {
        this.docker = docker;
        this.self = self;
        this.controllerId = controllerId;
        this.imageId = imageId;
        imageHash = Hash(imageId)[..12];
        this.windowsContainers = windowsContainers;
        this.stoppingToken = stoppingToken;
    }

    string ProbeProjectRoot {
        get => windowsContainers ? @"C:\compose-unity-probe" : "/compose-unity-probe";
    }

    string WorkerProjectRoot {
        get => windowsContainers ? @"C:\workspace\project" : "/var/workspace/project";
    }

    string ComposeExecutable {
        get => windowsContainers ? "compose-unity.exe" : "compose-unity";
    }

    public async ValueTask DisposeAsync() {
        await StopActiveWorkersAsync();
        await docker.DisposeAsync();
    }

    internal static Task<UnityMcpController> CreateAsync(CancellationToken stoppingToken) =>
        CreateAsync(new DockerEngineClient(), stoppingToken);

    internal static Task<UnityMcpController> CreateAsync(
        CancellationToken stoppingToken,
        string windowsPipeName,
        string unixSocketPath) =>
        CreateAsync(new DockerEngineClient(windowsPipeName, unixSocketPath), stoppingToken);

    static async Task<UnityMcpController> CreateAsync(DockerEngineClient docker, CancellationToken stoppingToken) {
        try {
            JsonObject version;
            try {
                version = await docker.VersionAsync(stoppingToken);
            } catch (Exception exception) when (exception is not OperationCanceledException) {
                string endpoint = OperatingSystem.IsWindows()
                    ? @"\\.\pipe\docker_engine"
                    : "/var/run/docker.sock";
                throw new InvalidOperationException(
                    $"MCP startup requires Docker Engine access at {endpoint}. " +
                    $"Mount the platform Docker socket or named pipe into the sidecar: {exception.Message}",
                    exception);
            }

            string daemonOs = version["Os"]?.GetValue<string>()
                              ?? throw new InvalidOperationException("Docker Engine did not report its container operating system.");
            var self = await docker.InspectSelfAsync(stoppingToken);
            string controllerId = self["Id"]?.GetValue<string>()
                                  ?? throw new InvalidOperationException("Docker Engine did not report the sidecar container ID.");
            string imageId = self["Image"]?.GetValue<string>()
                             ?? throw new InvalidOperationException("Docker Engine did not report the sidecar image ID.");
            return new UnityMcpController(
                docker,
                self,
                controllerId,
                imageId,
                daemonOs.Equals("windows", StringComparison.OrdinalIgnoreCase),
                stoppingToken);
        } catch {
            await docker.DisposeAsync();
            throw;
        }
    }

    internal async Task<object> ProjectInfoAsync(string projectRoot, CancellationToken cancellationToken) {
        var started = DateTimeOffset.UtcNow;
        var project = await GetProjectAsync(projectRoot, cancellationToken);
        LogStart("project_info", project.id);
        try {
            return new {
                projectRoot = project.normalizedRoot,
                project.probe.companyName,
                project.probe.projectName,
                project.probe.projectVersion,
                editor = new { version = project.probe.editorVersion, revision = project.probe.editorRevision },
                code = new { project.probe.apiCompatibility, project.probe.allowUnsafeCode, project.probe.scriptingBackendOverrides },
                rendering = new { project.probe.renderPipeline, project.probe.colorSpace, project.probe.graphicsApis },
                project.probe.inputHandling,
                project.probe.packages
            };
        } finally {
            LogEnd("project_info", project.id, started);
        }
    }

    internal async Task<object> RunTestsAsync(
        string projectRoot,
        string[] modes,
        CancellationToken cancellationToken) {
        if (modes is null || modes.Length == 0 || modes.Any(string.IsNullOrWhiteSpace)) {
            throw new ArgumentException("modes must be a non-empty array of non-empty strings.", nameof(modes));
        }

        if (modes.Any(mode => mode.Length > 128 || mode.Any(char.IsControl))) {
            throw new ArgumentException("Each test mode must be at most 128 characters and contain no control characters.", nameof(modes));
        }

        var project = await GetProjectAsync(projectRoot, cancellationToken);
        return await ExecuteSerializedAsync(project, "run_tests", async (worker, token) => {
            var command = new List<string> {
                ComposeExecutable,
                "exec",
                "unity-command",
                "--",
                "tests",
                "--junit",
                "-",
                WorkerProjectRoot
            };
            command.AddRange(modes);
            var result = await ExecuteWorkerAsync(worker, command, token);
            return BuildTestResult(result);
        }, cancellationToken);
    }

    internal async Task<object> ExecuteMethodAsync(
        string projectRoot,
        string method,
        string[]? arguments,
        CancellationToken cancellationToken) {
        if (string.IsNullOrWhiteSpace(method) || method.Length > 512 || method.Any(char.IsControl)) {
            throw new ArgumentException("method must be a non-empty static method name of at most 512 characters.", nameof(method));
        }

        arguments ??= [];
        if (arguments.Length > 256 || arguments.Any(argument => argument.Length > 16_384)) {
            throw new ArgumentException("arguments accepts at most 256 values of at most 16384 characters each.", nameof(arguments));
        }

        var project = await GetProjectAsync(projectRoot, cancellationToken);
        return await ExecuteSerializedAsync(project, "execute_method", async (worker, token) => {
            var command = new List<string> {
                ComposeExecutable,
                "exec",
                "unity-command",
                "--",
                "method",
                WorkerProjectRoot,
                method,
                "--"
            };
            command.AddRange(arguments);
            var result = await ExecuteWorkerAsync(worker, command, token);
            return new { exitStatus = result.exitCode, output = RelevantOutput(result.standardOutput), errorOutput = RelevantOutput(result.standardError) };
        }, cancellationToken);
    }

    internal async Task StopActiveWorkersAsync() {
        foreach (string worker in activeWorkers.Keys) {
            try {
                await docker.StopContainerAsync(worker, WorkerStopTimeout, CancellationToken.None);
            } catch (Exception exception) {
                Console.Error.WriteLine($"compose-unity-sidecar: failed to stop active MCP worker: {exception.Message}");
            }
        }
    }

    async Task<object> ExecuteSerializedAsync(
        ValidatedProject project,
        string tool,
        Func<WorkerContainer, CancellationToken, Task<object>> operation,
        CancellationToken cancellationToken) {
        var lane = lanes.GetOrAdd(project.id, _ => new AsyncFifoLock());
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stoppingToken);
        await using var laneLease = await lane.AcquireAsync(linked.Token);
        await using var daemonLease = await AcquireDaemonLockAsync(project, linked.Token);
        var started = DateTimeOffset.UtcNow;
        LogStart(tool, project.id);
        try {
            var worker = await EnsureWorkerAsync(project, linked.Token);
            return await operation(worker, linked.Token);
        } finally {
            LogEnd(tool, project.id, started);
        }
    }

    async Task<ExecResult> ExecuteWorkerAsync(
        WorkerContainer worker,
        IReadOnlyList<string> command,
        CancellationToken cancellationToken) {
        activeWorkers.TryAdd(worker.id, 0);
        try {
            return await docker.ExecAsync(worker.id, WorkerProjectRoot, command, cancellationToken);
        } catch (OperationCanceledException) {
            await docker.StopContainerAsync(worker.id, WorkerStopTimeout, CancellationToken.None);
            throw;
        } finally {
            activeWorkers.TryRemove(worker.id, out _);
        }
    }

    async Task<ValidatedProject> GetProjectAsync(string projectRoot, CancellationToken cancellationToken) {
        if (string.IsNullOrWhiteSpace(projectRoot) || projectRoot.Length > 4096 || projectRoot.Any(char.IsControl)) {
            throw new ArgumentException("projectRoot must be a non-empty Docker-daemon host path.", nameof(projectRoot));
        }

        var lazy = projects.GetOrAdd(
            projectRoot,
            value => new Lazy<Task<ValidatedProject>>(
                () => ProbeProjectAsync(value, stoppingToken),
                LazyThreadSafetyMode.ExecutionAndPublication));
        try {
            return await lazy.Value.WaitAsync(cancellationToken);
        } catch {
            if (lazy.IsValueCreated && lazy.Value.IsFaulted) {
                projects.TryRemove(new KeyValuePair<string, Lazy<Task<ValidatedProject>>>(projectRoot, lazy));
            }

            throw;
        }
    }

    async Task<ValidatedProject> ProbeProjectAsync(string suppliedRoot, CancellationToken cancellationToken) {
        string name = $"compose-unity-probe-{Guid.NewGuid():N}";
        string? containerId = null;
        try {
            var configuration = new JsonObject {
                ["Image"] = imageId,
                ["Cmd"] = DockerEngineClient.ToArray([ComposeExecutable, "sidecar", "probe-project", ProbeProjectRoot]),
                ["Labels"] = Labels("probe", null),
                ["HostConfig"] = new JsonObject {
                    ["Mounts"] = new JsonArray {
                        new JsonObject {
                            ["Type"] = "bind",
                            ["Source"] = suppliedRoot,
                            ["Target"] = ProbeProjectRoot,
                            ["ReadOnly"] = true,
                            ["BindOptions"] = new JsonObject { ["CreateMountpoint"] = false }
                        }
                    }
                }
            };
            containerId = await docker.CreateContainerAsync(name, configuration, cancellationToken);
            await docker.StartContainerAsync(containerId, cancellationToken);
            int exitCode = await docker.WaitContainerAsync(containerId, cancellationToken);
            var inspected = await docker.InspectContainerAsync(containerId, cancellationToken);
            var output = await docker.ContainerLogsAsync(containerId, cancellationToken);
            if (exitCode != 0) {
                string detail = RelevantOutput(output.standardError);
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                    ? "the validation probe failed without diagnostic output"
                    : detail);
            }

            var mount = inspected["Mounts"]?.AsArray()
                            .Select(node => node?.AsObject())
                            .FirstOrDefault(mount => mount?["Destination"]?.GetValue<string>() == ProbeProjectRoot)
                        ?? throw new InvalidOperationException("Docker did not report the project probe bind mount.");
            string normalizedRoot = mount["Source"]?.GetValue<string>()
                                    ?? throw new InvalidOperationException("Docker did not report the normalized project source path.");
            normalizedRoot = NormalizeDaemonPath(normalizedRoot);
            var probe = JsonSerializer.Deserialize<ProjectProbeResult>(
                            output.standardOutput,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                        ?? throw new InvalidOperationException("the project validation probe returned no project information");
            return new ValidatedProject(normalizedRoot, Hash(ProjectIdentityPath(normalizedRoot, windowsContainers)), probe);
        } catch (Exception exception) when (exception is not OperationCanceledException) {
            throw new InvalidOperationException(
                $"Unity project validation failed for '{suppliedRoot}': {exception.Message}",
                exception);
        } finally {
            if (containerId is not null) {
                await docker.RemoveContainerAsync(containerId, true, true, CancellationToken.None);
            }
        }
    }

    async Task<WorkerContainer> EnsureWorkerAsync(ValidatedProject project, CancellationToken cancellationToken) {
        string name = $"compose-unity-worker-{project.id[..16]}-{imageHash}";
        var inspected = await docker.TryInspectContainerAsync(name, cancellationToken);
        bool reused = inspected is not null;
        if (inspected is null) {
            try {
                await docker.CreateContainerAsync(name, BuildWorkerConfiguration(project), cancellationToken);
            } catch (DockerApiException exception) when (exception.statusCode == HttpStatusCode.Conflict) {
            }

            inspected = await docker.InspectContainerAsync(name, cancellationToken);
        }

        ValidateWorker(inspected, project);
        if (inspected["State"]?["Running"]?.GetValue<bool>() != true) {
            try {
                await docker.StartContainerAsync(name, cancellationToken);
            } catch (DockerApiException exception) when (exception.statusCode == HttpStatusCode.NotModified) {
            }
        }

        return new WorkerContainer(
            inspected["Id"]?.GetValue<string>() ?? name,
            name,
            reused);
    }

    JsonObject BuildWorkerConfiguration(ValidatedProject project) {
        var hostConfiguration = new JsonObject();
        var selfHostConfiguration = self["HostConfig"]?.AsObject() ?? new JsonObject();
        foreach (string name in InheritedHostConfigurationNames) {
            var value = selfHostConfiguration[name];
            if (value is not null) {
                hostConfiguration[name] = value.DeepClone();
            }
        }

        hostConfiguration["Mounts"] = BuildWorkerMounts(project);

        var labels = Labels("worker", project);
        labels[$"{LABEL_PREFIX}.image"] = imageId;
        return new JsonObject { ["Image"] = imageId, ["Env"] = ForwardedEnvironment(), ["Labels"] = labels, ["HostConfig"] = hostConfiguration };
    }

    JsonArray BuildWorkerMounts(ValidatedProject project) {
        var mounts = new JsonArray();
        foreach (string directory in new[] { "Assets", "Packages", "ProjectSettings" }) {
            mounts.Add(new JsonObject {
                ["Type"] = "bind",
                ["Source"] = CombineDaemonPath(project.normalizedRoot, directory, windowsContainers),
                ["Target"] = CombineContainerPath(WorkerProjectRoot, directory, windowsContainers),
                ["ReadOnly"] = false,
                ["BindOptions"] = new JsonObject { ["CreateMountpoint"] = false }
            });
        }

        var inheritedDestinations = windowsContainers
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
                @"C:\Program Files\Unity\Hub\Editor",
                @"C:\steam",
                @"C:\Users\ContainerAdministrator\AppData\Roaming\Unity",
                @"C:\Users\ContainerAdministrator\AppData\Roaming\UnityHub",
                @"C:\Users\ContainerAdministrator\AppData\Local\Unity",
                @"C:\ProgramData\Unity"
            }
            : new HashSet<string>(StringComparer.Ordinal) {
                "/root/Unity",
                "/root/Steam",
                "/root/.config/unity3d",
                "/root/.config/unityhub",
                "/root/.cache/Unity",
                "/root/.local/share/unity3d"
            };

        foreach (var node in self["Mounts"]?.AsArray() ?? []) {
            var mount = node?.AsObject();
            string? destination = mount?["Destination"]?.GetValue<string>();
            string? type = mount?["Type"]?.GetValue<string>();
            if (destination is null || type is null || !inheritedDestinations.Contains(destination)) {
                continue;
            }

            string? source = type.Equals("volume", StringComparison.OrdinalIgnoreCase)
                ? mount?["Name"]?.GetValue<string>()
                : mount?["Source"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(source)) {
                continue;
            }

            var inherited = new JsonObject { ["Type"] = type, ["Source"] = source, ["Target"] = destination, ["ReadOnly"] = mount?["RW"]?.GetValue<bool>() == false };
            string? propagation = mount?["Propagation"]?.GetValue<string>();
            if (type.Equals("bind", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(propagation)) {
                inherited["BindOptions"] = new JsonObject { ["Propagation"] = propagation };
            }

            mounts.Add(inherited);
        }

        return mounts;
    }

    JsonArray ForwardedEnvironment() {
        var allowed = new HashSet<string>(ForwardedEnvironmentNames, StringComparer.Ordinal);
        var result = new JsonArray();
        foreach (var node in self["Config"]?["Env"]?.AsArray() ?? []) {
            string? entry = node?.GetValue<string>();
            int separator = entry?.IndexOf('=') ?? -1;
            if (separator > 0 && allowed.Contains(entry![..separator])) {
                result.Add(entry);
            }
        }

        return result;
    }

    void ValidateWorker(JsonObject worker, ValidatedProject project) {
        var labels = worker["Config"]?["Labels"]?.AsObject();
        if (labels?[$"{LABEL_PREFIX}.kind"]?.GetValue<string>() != "worker"
            || labels[$"{LABEL_PREFIX}.project"]?.GetValue<string>() != project.id
            || worker["Image"]?.GetValue<string>() != imageId) {
            throw new InvalidOperationException("A conflicting container occupies the reserved MCP worker name.");
        }
    }

    async ValueTask<IAsyncDisposable> AcquireDaemonLockAsync(
        ValidatedProject project,
        CancellationToken cancellationToken) {
        string name = $"compose-unity-lock-{project.id[..32]}";
        while (true) {
            try {
                var configuration = new JsonObject { ["Image"] = imageId, ["Labels"] = Labels("lock", project) };
                await docker.CreateContainerAsync(name, configuration, cancellationToken);
                return new DockerContainerLease(docker, name);
            } catch (DockerApiException exception) when (exception.statusCode == HttpStatusCode.Conflict) {
                var existing = await docker.TryInspectContainerAsync(name, cancellationToken);
                if (existing is null) {
                    continue;
                }

                var labels = existing["Config"]?["Labels"]?.AsObject();
                if (labels?[$"{LABEL_PREFIX}.kind"]?.GetValue<string>() != "lock"
                    || labels[$"{LABEL_PREFIX}.project"]?.GetValue<string>() != project.id) {
                    throw new InvalidOperationException("A conflicting container occupies the reserved MCP project lock name.");
                }

                string? owner = labels[$"{LABEL_PREFIX}.controller"]?.GetValue<string>();
                if (owner == controllerId) {
                    await docker.RemoveContainerAsync(name, true, true, cancellationToken);
                    continue;
                }

                var ownerContainer = string.IsNullOrWhiteSpace(owner)
                    ? null
                    : await docker.TryInspectContainerAsync(owner, cancellationToken);
                if (ownerContainer?["State"]?["Running"]?.GetValue<bool>() != true) {
                    await docker.RemoveContainerAsync(name, true, true, cancellationToken);
                    continue;
                }

                await Task.Delay(200, cancellationToken);
            }
        }
    }

    JsonObject Labels(string kind, ValidatedProject? project) {
        var labels = new JsonObject { [$"{LABEL_PREFIX}.kind"] = kind, [$"{LABEL_PREFIX}.controller"] = controllerId };
        if (project is not null) {
            labels[$"{LABEL_PREFIX}.project"] = project.id;
            labels[$"{LABEL_PREFIX}.project-root"] = project.normalizedRoot;
        }

        return labels;
    }

    internal static object BuildTestResult(ExecResult result) {
        try {
            var document = XDocument.Parse(result.standardOutput, LoadOptions.None);
            var suites = document.Descendants("testsuite").ToList();
            if (suites.Count == 0) {
                throw new InvalidOperationException("The JUnit report contains no test suites.");
            }

            var testCases = document.Descendants("testcase").ToList();
            int total = AttributeSum(suites, "tests", testCases.Count);
            int failureCount = AttributeSum(suites, "failures", testCases.Count(test => test.Element("failure") is not null));
            int errorCount = AttributeSum(suites, "errors", testCases.Count(test => test.Element("error") is not null));
            int skipped = AttributeSum(suites, "skipped", testCases.Count(test => test.Element("skipped") is not null));
            decimal duration = suites.Sum(suite => ParseDecimal(suite.Attribute("time")?.Value));
            var allFailureDetails = testCases
                .Select(test => new { test, failure = test.Element("failure") ?? test.Element("error") })
                .Where(item => item.failure is not null)
                .ToList();
            var failureDetails = allFailureDetails
                .Take(100)
                .Select(item => new {
                    name = item.test.Attribute("name")?.Value,
                    className = item.test.Attribute("classname")?.Value,
                    message = item.failure!.Attribute("message")?.Value,
                    type = item.failure.Attribute("type")?.Value,
                    stackTrace = item.failure.Value
                })
                .ToList();

            if (failureCount == 0 && errorCount == 0 && result.exitCode != 0) {
                return ErrorTestResult(result);
            }

            string outcome = failureCount == 0 && errorCount == 0 ? "passed" : "failed";
            return new {
                outcome,
                result.exitCode,
                counts = new {
                    total,
                    passed = Math.Max(0, total - failureCount - errorCount - skipped),
                    failures = failureCount,
                    errors = errorCount,
                    skipped
                },
                durationSeconds = decimal.Round(duration, 3),
                failures = failureDetails,
                failuresTruncated = failureCount + errorCount > failureDetails.Count
            };
        } catch (Exception exception) when (exception is XmlException or InvalidOperationException) {
            return ErrorTestResult(result);
        }
    }

    static object ErrorTestResult(ExecResult result) => new { outcome = "error", result.exitCode, log = result.combinedOutput };

    static int AttributeSum(IReadOnlyList<XElement> suites, string name, int fallback) {
        if (suites.Count == 0 || suites.Any(suite => suite.Attribute(name) is null)) {
            return fallback;
        }

        return suites.Sum(suite => int.TryParse(suite.Attribute(name)?.Value, out int value) ? value : 0);
    }

    static decimal ParseDecimal(string? value) =>
        decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal result) ? result : 0;

    static string RelevantOutput(string value, int maximumLength = 64 * 1024) {
        string trimmed = value.Trim();
        return trimmed.Length <= maximumLength
            ? trimmed
            : trimmed[..maximumLength] + "\n[output truncated]";
    }

    internal static string NormalizeDaemonPath(string path) {
        int rootLength = Path.GetPathRoot(path)?.Length ?? 0;
        while (path.Length > rootLength && (path.EndsWith('/') || path.EndsWith('\\'))) {
            path = path[..^1];
        }

        return path;
    }

    internal static string ProjectIdentityPath(string path, bool windowsContainers) =>
        windowsContainers || LooksLikeWindowsHostPath(path) ? path.ToUpperInvariant() : path;

    internal static bool LooksLikeWindowsHostPath(string path) =>
        path.Length >= 3
        && char.IsAsciiLetter(path[0])
        && path[1] == ':'
        && path[2] is '\\' or '/';

    internal static string CombineDaemonPath(string root, string child, bool windowsContainers) =>
        windowsContainers ? Path.Combine(root, child) : root + "/" + child;

    internal static string CombineContainerPath(string root, string child, bool windowsContainers) =>
        windowsContainers ? root + "\\" + child : root + "/" + child;

    static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    static void LogStart(string tool, string project) =>
        Console.WriteLine($"MCP START tool={tool} project={project[..12]}");

    static void LogEnd(string tool, string project, DateTimeOffset started) =>
        Console.WriteLine($"MCP END tool={tool} project={project[..12]} duration={(DateTimeOffset.UtcNow - started).TotalSeconds:F1}s");
}

sealed record ValidatedProject(string normalizedRoot, string id, ProjectProbeResult probe);

sealed record WorkerContainer(string id, string name, bool reused);

sealed class DockerContainerLease(DockerEngineClient docker, string name) : IAsyncDisposable {
    public async ValueTask DisposeAsync() =>
        await docker.RemoveContainerAsync(name, true, true, CancellationToken.None);
}

sealed class AsyncFifoLock {
    readonly object gate = new();
    readonly Queue<Waiter> waiters = new();
    bool held;

    internal ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken) {
        lock (gate) {
            if (!held) {
                held = true;
                return ValueTask.FromResult<IAsyncDisposable>(new Lease(this));
            }

            var waiter = new Waiter(this, cancellationToken);
            waiters.Enqueue(waiter);
            return new ValueTask<IAsyncDisposable>(waiter.task);
        }
    }

    void Release() {
        lock (gate) {
            while (waiters.Count > 0) {
                if (waiters.Dequeue().TryAcquire()) {
                    return;
                }
            }

            held = false;
        }
    }

    sealed class Waiter {
        readonly TaskCompletionSource<IAsyncDisposable> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly AsyncFifoLock owner;
        readonly CancellationTokenRegistration registration;

        internal Waiter(AsyncFifoLock owner, CancellationToken cancellationToken) {
            this.owner = owner;
            registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        }

        internal Task<IAsyncDisposable> task {
            get => completion.Task;
        }

        internal bool TryAcquire() {
            bool acquired = completion.TrySetResult(new Lease(owner));
            registration.Dispose();
            return acquired;
        }
    }

    sealed class Lease(AsyncFifoLock owner) : IAsyncDisposable {
        int released;

        public ValueTask DisposeAsync() {
            if (Interlocked.Exchange(ref released, 1) == 0) {
                owner.Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}
