using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;

internal sealed class UnityMcpController : IAsyncDisposable
{
    private const string LabelPrefix = "net.slothsoft.compose-unity";
    private static readonly TimeSpan WorkerStopTimeout = TimeSpan.FromSeconds(10);
    private static readonly string[] ForwardedEnvironmentNames =
    [
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
    private static readonly string[] InheritedHostConfigurationNames =
    [
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

    private readonly DockerEngineClient docker;
    private readonly JsonObject self;
    private readonly string controllerId;
    private readonly string imageId;
    private readonly string imageHash;
    private readonly bool windowsContainers;
    private readonly CancellationToken stoppingToken;
    private readonly ConcurrentDictionary<string, Lazy<Task<ValidatedProject>>> projects = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AsyncFifoLock> lanes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> activeWorkers = new(StringComparer.Ordinal);

    private UnityMcpController(
        DockerEngineClient docker,
        JsonObject self,
        string controllerId,
        string imageId,
        bool windowsContainers,
        CancellationToken stoppingToken)
    {
        this.docker = docker;
        this.self = self;
        this.controllerId = controllerId;
        this.imageId = imageId;
        imageHash = Hash(imageId)[..12];
        this.windowsContainers = windowsContainers;
        this.stoppingToken = stoppingToken;
    }

    internal static async Task<UnityMcpController> CreateAsync(CancellationToken stoppingToken)
    {
        var docker = new DockerEngineClient();
        try
        {
            JsonObject version;
            try
            {
                version = await docker.VersionAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var endpoint = OperatingSystem.IsWindows()
                    ? @"\\.\pipe\docker_engine"
                    : "/var/run/docker.sock";
                throw new InvalidOperationException(
                    $"MCP startup requires Docker Engine access at {endpoint}. " +
                    $"Mount the platform Docker socket or named pipe into the sidecar: {exception.Message}",
                    exception);
            }
            var daemonOs = version["Os"]?.GetValue<string>()
                ?? throw new InvalidOperationException("Docker Engine did not report its container operating system.");
            var self = await docker.InspectSelfAsync(stoppingToken);
            var controllerId = self["Id"]?.GetValue<string>()
                ?? throw new InvalidOperationException("Docker Engine did not report the sidecar container ID.");
            var imageId = self["Image"]?.GetValue<string>()
                ?? throw new InvalidOperationException("Docker Engine did not report the sidecar image ID.");
            return new UnityMcpController(
                docker,
                self,
                controllerId,
                imageId,
                daemonOs.Equals("windows", StringComparison.OrdinalIgnoreCase),
                stoppingToken);
        }
        catch
        {
            await docker.DisposeAsync();
            throw;
        }
    }

    internal async Task<object> ProjectInfoAsync(string projectRoot, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var project = await GetProjectAsync(projectRoot, cancellationToken);
        LogStart("project_info", project.Id);
        try
        {
            return new
            {
                projectRoot = project.NormalizedRoot,
                companyName = project.Probe.CompanyName,
                projectName = project.Probe.ProjectName,
                projectVersion = project.Probe.ProjectVersion,
                editor = new
                {
                    version = project.Probe.EditorVersion,
                    revision = project.Probe.EditorRevision
                },
                code = new
                {
                    apiCompatibility = project.Probe.ApiCompatibility,
                    allowUnsafeCode = project.Probe.AllowUnsafeCode,
                    scriptingBackendOverrides = project.Probe.ScriptingBackendOverrides
                },
                rendering = new
                {
                    renderPipeline = project.Probe.RenderPipeline,
                    colorSpace = project.Probe.ColorSpace,
                    graphicsApis = project.Probe.GraphicsApis
                },
                inputHandling = project.Probe.InputHandling,
                packages = project.Probe.Packages
            };
        }
        finally
        {
            LogEnd("project_info", project.Id, started);
        }
    }

    internal async Task<object> RunTestsAsync(
        string projectRoot,
        string[] modes,
        CancellationToken cancellationToken)
    {
        if (modes is null || modes.Length == 0 || modes.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("modes must be a non-empty array of non-empty strings.", nameof(modes));
        }
        if (modes.Any(mode => mode.Length > 128 || mode.Any(char.IsControl)))
        {
            throw new ArgumentException("Each test mode must be at most 128 characters and contain no control characters.", nameof(modes));
        }

        var project = await GetProjectAsync(projectRoot, cancellationToken);
        return await ExecuteSerializedAsync(project, "run_tests", async (worker, token) =>
        {
            var command = new List<string>
            {
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
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(method) || method.Length > 512 || method.Any(char.IsControl))
        {
            throw new ArgumentException("method must be a non-empty static method name of at most 512 characters.", nameof(method));
        }
        arguments ??= [];
        if (arguments.Length > 256 || arguments.Any(argument => argument is null || argument.Length > 16_384))
        {
            throw new ArgumentException("arguments accepts at most 256 values of at most 16384 characters each.", nameof(arguments));
        }

        var project = await GetProjectAsync(projectRoot, cancellationToken);
        return await ExecuteSerializedAsync(project, "execute_method", async (worker, token) =>
        {
            var command = new List<string>
            {
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
            return new
            {
                exitStatus = result.ExitCode,
                output = RelevantOutput(result.StandardOutput),
                errorOutput = RelevantOutput(result.StandardError)
            };
        }, cancellationToken);
    }

    internal async Task StopActiveWorkersAsync()
    {
        foreach (var worker in activeWorkers.Keys)
        {
            try
            {
                await docker.StopContainerAsync(worker, WorkerStopTimeout, CancellationToken.None);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"compose-unity-sidecar: failed to stop active MCP worker: {exception.Message}");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopActiveWorkersAsync();
        await docker.DisposeAsync();
    }

    private async Task<object> ExecuteSerializedAsync(
        ValidatedProject project,
        string tool,
        Func<WorkerContainer, CancellationToken, Task<object>> operation,
        CancellationToken cancellationToken)
    {
        var lane = lanes.GetOrAdd(project.Id, _ => new AsyncFifoLock());
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stoppingToken);
        await using var laneLease = await lane.AcquireAsync(linked.Token);
        await using var daemonLease = await AcquireDaemonLockAsync(project, linked.Token);
        var started = DateTimeOffset.UtcNow;
        LogStart(tool, project.Id);
        try
        {
            var worker = await EnsureWorkerAsync(project, linked.Token);
            return await operation(worker, linked.Token);
        }
        finally
        {
            LogEnd(tool, project.Id, started);
        }
    }

    private async Task<ExecResult> ExecuteWorkerAsync(
        WorkerContainer worker,
        IReadOnlyList<string> command,
        CancellationToken cancellationToken)
    {
        activeWorkers.TryAdd(worker.Id, 0);
        try
        {
            return await docker.ExecAsync(worker.Id, WorkerProjectRoot, command, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await docker.StopContainerAsync(worker.Id, WorkerStopTimeout, CancellationToken.None);
            throw;
        }
        finally
        {
            activeWorkers.TryRemove(worker.Id, out _);
        }
    }

    private async Task<ValidatedProject> GetProjectAsync(string projectRoot, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectRoot) || projectRoot.Length > 4096 || projectRoot.Any(char.IsControl))
        {
            throw new ArgumentException("projectRoot must be a non-empty Docker-daemon host path.", nameof(projectRoot));
        }

        var lazy = projects.GetOrAdd(
            projectRoot,
            value => new Lazy<Task<ValidatedProject>>(
                () => ProbeProjectAsync(value, stoppingToken),
                LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await lazy.Value.WaitAsync(cancellationToken);
        }
        catch
        {
            if (lazy.IsValueCreated && lazy.Value.IsFaulted)
            {
                projects.TryRemove(new KeyValuePair<string, Lazy<Task<ValidatedProject>>>(projectRoot, lazy));
            }
            throw;
        }
    }

    private async Task<ValidatedProject> ProbeProjectAsync(string suppliedRoot, CancellationToken cancellationToken)
    {
        var name = $"compose-unity-probe-{Guid.NewGuid():N}";
        string? containerId = null;
        try
        {
            var configuration = new JsonObject
            {
                ["Image"] = imageId,
                ["Cmd"] = DockerEngineClient.ToArray([SidecarExecutable, "probe-project", ProbeProjectRoot]),
                ["Labels"] = Labels("probe", null),
                ["HostConfig"] = new JsonObject
                {
                    ["Mounts"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["Type"] = "bind",
                            ["Source"] = suppliedRoot,
                            ["Target"] = ProbeProjectRoot,
                            ["ReadOnly"] = true,
                            ["BindOptions"] = new JsonObject
                            {
                                ["CreateMountpoint"] = false
                            }
                        }
                    }
                }
            };
            containerId = await docker.CreateContainerAsync(name, configuration, cancellationToken);
            await docker.StartContainerAsync(containerId, cancellationToken);
            var exitCode = await docker.WaitContainerAsync(containerId, cancellationToken);
            var inspected = await docker.InspectContainerAsync(containerId, cancellationToken);
            var output = await docker.ContainerLogsAsync(containerId, cancellationToken);
            if (exitCode != 0)
            {
                var detail = RelevantOutput(output.StandardError);
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                    ? "the validation probe failed without diagnostic output"
                    : detail);
            }

            var mount = inspected["Mounts"]?.AsArray()
                .Select(node => node?.AsObject())
                .FirstOrDefault(mount => mount?["Destination"]?.GetValue<string>() == ProbeProjectRoot)
                ?? throw new InvalidOperationException("Docker did not report the project probe bind mount.");
            var normalizedRoot = mount["Source"]?.GetValue<string>()
                ?? throw new InvalidOperationException("Docker did not report the normalized project source path.");
            normalizedRoot = NormalizeDaemonPath(normalizedRoot);
            var probe = JsonSerializer.Deserialize<ProjectProbeResult>(
                output.StandardOutput,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("the project validation probe returned no project information");
            return new ValidatedProject(normalizedRoot, Hash(ProjectIdentityPath(normalizedRoot)), probe);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Unity project validation failed for '{suppliedRoot}': {exception.Message}",
                exception);
        }
        finally
        {
            if (containerId is not null)
            {
                await docker.RemoveContainerAsync(containerId, true, true, CancellationToken.None);
            }
        }
    }

    private async Task<WorkerContainer> EnsureWorkerAsync(ValidatedProject project, CancellationToken cancellationToken)
    {
        var name = $"compose-unity-worker-{project.Id[..16]}-{imageHash}";
        var inspected = await docker.TryInspectContainerAsync(name, cancellationToken);
        var reused = inspected is not null;
        if (inspected is null)
        {
            try
            {
                await docker.CreateContainerAsync(name, BuildWorkerConfiguration(project), cancellationToken);
            }
            catch (DockerApiException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
            {
            }
            inspected = await docker.InspectContainerAsync(name, cancellationToken);
        }

        ValidateWorker(inspected, project);
        if (inspected["State"]?["Running"]?.GetValue<bool>() != true)
        {
            try
            {
                await docker.StartContainerAsync(name, cancellationToken);
            }
            catch (DockerApiException exception) when (exception.StatusCode == HttpStatusCode.NotModified)
            {
            }
        }
        return new WorkerContainer(
            inspected["Id"]?.GetValue<string>() ?? name,
            name,
            reused);
    }

    private JsonObject BuildWorkerConfiguration(ValidatedProject project)
    {
        var hostConfiguration = new JsonObject();
        var selfHostConfiguration = self["HostConfig"]?.AsObject() ?? new JsonObject();
        foreach (var name in InheritedHostConfigurationNames)
        {
            if (selfHostConfiguration[name] is JsonNode value)
            {
                hostConfiguration[name] = value.DeepClone();
            }
        }
        hostConfiguration["Mounts"] = BuildWorkerMounts(project);

        var labels = Labels("worker", project);
        labels[$"{LabelPrefix}.image"] = imageId;
        return new JsonObject
        {
            ["Image"] = imageId,
            ["Env"] = ForwardedEnvironment(),
            ["Labels"] = labels,
            ["HostConfig"] = hostConfiguration
        };
    }

    private JsonArray BuildWorkerMounts(ValidatedProject project)
    {
        var mounts = new JsonArray();
        foreach (var directory in new[] { "Assets", "Packages", "ProjectSettings" })
        {
            mounts.Add(new JsonObject
            {
                ["Type"] = "bind",
                ["Source"] = CombineDaemonPath(project.NormalizedRoot, directory),
                ["Target"] = CombineContainerPath(WorkerProjectRoot, directory),
                ["ReadOnly"] = false,
                ["BindOptions"] = new JsonObject
                {
                    ["CreateMountpoint"] = false
                }
            });
        }

        var inheritedDestinations = windowsContainers
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                @"C:\Program Files\Unity\Hub\Editor",
                @"C:\steam",
                @"C:\Users\ContainerAdministrator\AppData\Roaming\Unity",
                @"C:\Users\ContainerAdministrator\AppData\Roaming\UnityHub",
                @"C:\Users\ContainerAdministrator\AppData\Local\Unity",
                @"C:\ProgramData\Unity"
            }
            : new HashSet<string>(StringComparer.Ordinal)
            {
                "/root/Unity",
                "/root/Steam",
                "/root/.config/unity3d",
                "/root/.config/unityhub",
                "/root/.cache/Unity",
                "/root/.local/share/unity3d"
            };

        foreach (var node in self["Mounts"]?.AsArray() ?? [])
        {
            var mount = node?.AsObject();
            var destination = mount?["Destination"]?.GetValue<string>();
            var type = mount?["Type"]?.GetValue<string>();
            if (destination is null || type is null || !inheritedDestinations.Contains(destination))
            {
                continue;
            }

            var source = type.Equals("volume", StringComparison.OrdinalIgnoreCase)
                ? mount?["Name"]?.GetValue<string>()
                : mount?["Source"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(source))
            {
                continue;
            }

            var inherited = new JsonObject
            {
                ["Type"] = type,
                ["Source"] = source,
                ["Target"] = destination,
                ["ReadOnly"] = mount?["RW"]?.GetValue<bool>() == false
            };
            var propagation = mount?["Propagation"]?.GetValue<string>();
            if (type.Equals("bind", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(propagation))
            {
                inherited["BindOptions"] = new JsonObject { ["Propagation"] = propagation };
            }
            mounts.Add(inherited);
        }
        return mounts;
    }

    private JsonArray ForwardedEnvironment()
    {
        var allowed = new HashSet<string>(ForwardedEnvironmentNames, StringComparer.Ordinal);
        var result = new JsonArray();
        foreach (var node in self["Config"]?["Env"]?.AsArray() ?? [])
        {
            var entry = node?.GetValue<string>();
            var separator = entry?.IndexOf('=') ?? -1;
            if (separator > 0 && allowed.Contains(entry![..separator]))
            {
                result.Add(entry);
            }
        }
        return result;
    }

    private void ValidateWorker(JsonObject worker, ValidatedProject project)
    {
        var labels = worker["Config"]?["Labels"]?.AsObject();
        if (labels?[$"{LabelPrefix}.kind"]?.GetValue<string>() != "worker"
            || labels[$"{LabelPrefix}.project"]?.GetValue<string>() != project.Id
            || worker["Image"]?.GetValue<string>() != imageId)
        {
            throw new InvalidOperationException("A conflicting container occupies the reserved MCP worker name.");
        }
    }

    private async ValueTask<IAsyncDisposable> AcquireDaemonLockAsync(
        ValidatedProject project,
        CancellationToken cancellationToken)
    {
        var name = $"compose-unity-lock-{project.Id[..32]}";
        while (true)
        {
            try
            {
                var configuration = new JsonObject
                {
                    ["Image"] = imageId,
                    ["Labels"] = Labels("lock", project)
                };
                await docker.CreateContainerAsync(name, configuration, cancellationToken);
                return new DockerContainerLease(docker, name);
            }
            catch (DockerApiException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
            {
                var existing = await docker.TryInspectContainerAsync(name, cancellationToken);
                if (existing is null)
                {
                    continue;
                }
                var labels = existing["Config"]?["Labels"]?.AsObject();
                if (labels?[$"{LabelPrefix}.kind"]?.GetValue<string>() != "lock"
                    || labels[$"{LabelPrefix}.project"]?.GetValue<string>() != project.Id)
                {
                    throw new InvalidOperationException("A conflicting container occupies the reserved MCP project lock name.");
                }

                var owner = labels[$"{LabelPrefix}.controller"]?.GetValue<string>();
                var ownerContainer = string.IsNullOrWhiteSpace(owner)
                    ? null
                    : await docker.TryInspectContainerAsync(owner, cancellationToken);
                if (ownerContainer?["State"]?["Running"]?.GetValue<bool>() != true)
                {
                    await docker.RemoveContainerAsync(name, true, true, cancellationToken);
                    continue;
                }

                await Task.Delay(200, cancellationToken);
            }
        }
    }

    private JsonObject Labels(string kind, ValidatedProject? project)
    {
        var labels = new JsonObject
        {
            [$"{LabelPrefix}.kind"] = kind,
            [$"{LabelPrefix}.controller"] = controllerId
        };
        if (project is not null)
        {
            labels[$"{LabelPrefix}.project"] = project.Id;
            labels[$"{LabelPrefix}.project-root"] = project.NormalizedRoot;
        }
        return labels;
    }

    private static object BuildTestResult(ExecResult result)
    {
        try
        {
            var document = XDocument.Parse(result.StandardOutput, LoadOptions.None);
            var suites = document.Descendants("testsuite").ToList();
            if (suites.Count == 0)
            {
                throw new InvalidOperationException("The JUnit report contains no test suites.");
            }
            var testCases = document.Descendants("testcase").ToList();
            var total = AttributeSum(suites, "tests", testCases.Count);
            var failureCount = AttributeSum(suites, "failures", testCases.Count(test => test.Element("failure") is not null));
            var errorCount = AttributeSum(suites, "errors", testCases.Count(test => test.Element("error") is not null));
            var skipped = AttributeSum(suites, "skipped", testCases.Count(test => test.Element("skipped") is not null));
            var duration = suites.Sum(suite => ParseDecimal(suite.Attribute("time")?.Value));
            var allFailureDetails = testCases
                .Select(test => new { test, failure = test.Element("failure") ?? test.Element("error") })
                .Where(item => item.failure is not null)
                .ToList();
            var failureDetails = allFailureDetails
                .Take(100)
                .Select(item => new
                {
                    name = item.test.Attribute("name")?.Value,
                    className = item.test.Attribute("classname")?.Value,
                    message = item.failure!.Attribute("message")?.Value,
                    type = item.failure.Attribute("type")?.Value,
                    stackTrace = item.failure.Value
                })
                .ToList();

            if (failureCount == 0 && errorCount == 0 && result.ExitCode != 0)
            {
                return ErrorTestResult(result);
            }

            var outcome = failureCount == 0 && errorCount == 0 ? "passed" : "failed";
            return new
            {
                outcome,
                exitCode = result.ExitCode,
                counts = new
                {
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
        }
        catch (Exception exception) when (exception is XmlException or InvalidOperationException)
        {
            return ErrorTestResult(result);
        }
    }

    private static object ErrorTestResult(ExecResult result) => new
    {
        outcome = "error",
        exitCode = result.ExitCode,
        log = result.CombinedOutput
    };

    private static int AttributeSum(IReadOnlyList<XElement> suites, string name, int fallback)
    {
        if (suites.Count == 0 || suites.Any(suite => suite.Attribute(name) is null))
        {
            return fallback;
        }
        return suites.Sum(suite => int.TryParse(suite.Attribute(name)?.Value, out var value) ? value : 0);
    }

    private static decimal ParseDecimal(string? value) =>
        decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : 0;

    private static string RelevantOutput(string value, int maximumLength = 64 * 1024)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= maximumLength
            ? trimmed
            : trimmed[..maximumLength] + "\n[output truncated]";
    }

    private static string NormalizeDaemonPath(string path)
    {
        var rootLength = Path.GetPathRoot(path)?.Length ?? 0;
        while (path.Length > rootLength && (path.EndsWith('/') || path.EndsWith('\\')))
        {
            path = path[..^1];
        }
        return path;
    }

    private string ProjectIdentityPath(string path) =>
        windowsContainers || LooksLikeWindowsHostPath(path) ? path.ToUpperInvariant() : path;

    private static bool LooksLikeWindowsHostPath(string path) =>
        path.Length >= 3
        && char.IsAsciiLetter(path[0])
        && path[1] == ':'
        && path[2] is '\\' or '/';

    private string CombineDaemonPath(string root, string child) =>
        windowsContainers ? Path.Combine(root, child) : root + "/" + child;

    private string CombineContainerPath(string root, string child) =>
        windowsContainers ? root + "\\" + child : root + "/" + child;

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void LogStart(string tool, string project) =>
        Console.WriteLine($"MCP START tool={tool} project={project[..12]}");

    private static void LogEnd(string tool, string project, DateTimeOffset started) =>
        Console.WriteLine($"MCP END tool={tool} project={project[..12]} duration={(DateTimeOffset.UtcNow - started).TotalSeconds:F1}s");

    private string ProbeProjectRoot => windowsContainers ? @"C:\compose-unity-probe" : "/compose-unity-probe";
    private string WorkerProjectRoot => windowsContainers ? @"C:\workspace\project" : "/var/workspace/project";
    private string SidecarExecutable => windowsContainers ? "compose-unity-sidecar.exe" : "compose-unity-sidecar";
    private string ComposeExecutable => windowsContainers ? "compose-unity.exe" : "compose-unity";
}

internal sealed record ValidatedProject(string NormalizedRoot, string Id, ProjectProbeResult Probe);

internal sealed record WorkerContainer(string Id, string Name, bool Reused);

internal sealed class DockerContainerLease(DockerEngineClient docker, string name) : IAsyncDisposable
{
    public async ValueTask DisposeAsync() =>
        await docker.RemoveContainerAsync(name, true, true, CancellationToken.None);
}

internal sealed class AsyncFifoLock
{
    private readonly object gate = new();
    private readonly Queue<Waiter> waiters = new();
    private bool held;

    internal ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!held)
            {
                held = true;
                return ValueTask.FromResult<IAsyncDisposable>(new Lease(this));
            }

            var waiter = new Waiter(this, cancellationToken);
            waiters.Enqueue(waiter);
            return new ValueTask<IAsyncDisposable>(waiter.Task);
        }
    }

    private void Release()
    {
        lock (gate)
        {
            while (waiters.Count > 0)
            {
                if (waiters.Dequeue().TryAcquire())
                {
                    return;
                }
            }
            held = false;
        }
    }

    private sealed class Waiter
    {
        private readonly TaskCompletionSource<IAsyncDisposable> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly AsyncFifoLock owner;
        private readonly CancellationTokenRegistration registration;

        internal Waiter(AsyncFifoLock owner, CancellationToken cancellationToken)
        {
            this.owner = owner;
            registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        }

        internal Task<IAsyncDisposable> Task => completion.Task;

        internal bool TryAcquire()
        {
            var acquired = completion.TrySetResult(new Lease(owner));
            registration.Dispose();
            return acquired;
        }
    }

    private sealed class Lease(AsyncFifoLock owner) : IAsyncDisposable
    {
        private int released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                owner.Release();
            }
            return ValueTask.CompletedTask;
        }
    }
}
