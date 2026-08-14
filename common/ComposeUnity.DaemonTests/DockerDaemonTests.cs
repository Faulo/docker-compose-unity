using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ComposeUnity.DaemonTests;

[Category("DockerDaemon")]
[TestFixture("linux", "linux")]
[TestFixture("windows", "windows")]
[CancelAfter(10 * 60 * 1000)]
public sealed partial class DockerDaemonTests(string expectedOs, string dockerContext) {
    readonly string id = Guid.NewGuid().ToString("N");
    string repository = string.Empty;
    string project = string.Empty;
    string staging = string.Empty;
    string image = string.Empty;
    string container = string.Empty;
    string? controllerId;
    HttpClient? httpClient;
    Uri? endpoint;
    int requestId;
    bool imageBuilt;
    bool controllerStarted;

    [OneTimeSetUp]
    public async Task SetUpDaemonAsync() {
        repository = FindRepository();
        project = Path.Combine(repository, "common", "ComposeUnity.Tests", "test-files", "ValidProject");
        staging = Path.Combine(Path.GetTempPath(), $"compose-unity-daemon-tests-{id}");
        image = $"tmp/compose-unity-daemon-tests:{id}";
        container = $"tmp-compose-unity-daemon-{id}";

        var inspection = await RunAsync("docker", ["context", "inspect", dockerContext, "--format", "{{.Endpoints.docker.Host}}"], TimeSpan.FromSeconds(5));
        if (inspection.exitCode != 0) {
            Assert.Ignore($"Docker context '{dockerContext}' is unavailable: {Detail(inspection)}");
        }

        string contextEndpoint = inspection.standardOutput.Trim();
        if (!IsLocalEndpoint(contextEndpoint)) {
            Assert.Ignore($"Docker context '{dockerContext}' is not local: {contextEndpoint}");
        }

        var version = await RunDockerAsync(["version", "--format", "{{.Server.Os}}"], TimeSpan.FromSeconds(5));
        if (version.exitCode != 0) {
            Assert.Ignore($"Docker context '{dockerContext}' is offline: {Detail(version)}");
        }

        Assert.That(version.standardOutput.Trim(), Is.EqualTo(expectedOs).IgnoreCase,
            $"Docker context '{dockerContext}' does not target the expected container OS.");

        try {
            Directory.CreateDirectory(Path.Combine(staging, "controller"));
            Directory.CreateDirectory(Path.Combine(staging, "backend"));
            string runtime = expectedOs == "windows" ? "win-x64" : "linux-x64";
            await PublishAsync(Path.Combine(repository, "common", "ComposeUnity", "ComposeUnity.csproj"), runtime, Path.Combine(staging, "controller"));
            await PublishAsync(Path.Combine(repository, "common", "ComposeUnity.DaemonTests.Backend", "ComposeUnity.DaemonTests.Backend.csproj"), runtime, Path.Combine(staging, "backend"));

            string dockerfile = Path.Combine(repository, "common", "ComposeUnity.DaemonTests", $"Dockerfile.{expectedOs}");
            AssertDockerSucceeded(await RunDockerAsync(["build", "--tag", image, "--file", dockerfile, staging], TimeSpan.FromMinutes(5)), "build daemon-test image");
            imageBuilt = true;

            string dockerMount = expectedOs == "windows"
                ? WindowsDockerMount(contextEndpoint)
                : "type=bind,source=/var/run/docker.sock,target=/var/run/docker.sock";
            var runArguments = new List<string> {
                "run", "--detach", "--name", container, "--env", "COMPOSE_UNITY_MCP=1"
            };
            if (expectedOs == "windows") {
                runArguments.AddRange(["--isolation", "hyperv"]);
            } else {
                runArguments.AddRange(["--publish", "127.0.0.1::8080"]);
            }
            runArguments.AddRange(["--mount", dockerMount, image]);
            AssertDockerSucceeded(await RunDockerAsync(runArguments, TimeSpan.FromMinutes(1)), "start daemon-test controller");
            controllerStarted = true;

            var controllerInspection = await DockerCheckedAsync(["inspect", "--format", "{{.Id}}", container]);
            controllerId = controllerInspection.standardOutput.Trim();
            await WaitForHealthyControllerAsync();
            await ConfigureMcpClientAsync();
            await InitializeMcpAsync();
        } catch {
            await CleanupAsync();
            throw;
        }
    }

    [OneTimeTearDown]
    public async Task TearDownDaemonAsync() {
        httpClient?.Dispose();
        await CleanupAsync();
    }

    [Test]
    public async Task AdvertisesToolsAndReportsProjectInformation() {
        var tools = await InvokeMcpAsync("tools/list", new JsonObject());
        string[] names = tools["result"]!["tools"]!.AsArray()
            .Select(tool => tool!["name"]!.GetValue<string>())
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.That(names, Is.EqualTo(new[] { "execute_method", "get_project_info", "run_tests" }));

        var response = await CallToolAsync("get_project_info", new JsonObject { ["projectRoot"] = project });
        Assert.That(response["result"]!["isError"]?.GetValue<bool>(), Is.Not.True, response.ToJsonString());
        var result = ToolResult(response);
        using (Assert.EnterMultipleScope()) {
            Assert.That(result["projectRoot"]!.GetValue<string>(), Is.EqualTo(project));
            Assert.That(result["projectName"]!.GetValue<string>(), Is.EqualTo("Example Game"));
            Assert.That(result["editor"]!["version"]!.GetValue<string>(), Is.EqualTo("6000.3.13f1"));
            Assert.That(result["rendering"]!["renderPipeline"]!.GetValue<string>(), Is.EqualTo("Universal"));
            Assert.That(result["packages"]!["custom"]!.GetValue<string>(), Is.EqualTo("1.2.3"));
        }
    }

    [Test]
    public async Task PreservesMethodArgumentsOutputAndExitStatus() {
        string[] arguments = [string.Empty, "two words", "--", "\"quoted\""];
        var response = await CallToolAsync("execute_method", new JsonObject {
            ["projectRoot"] = project,
            ["method"] = "DaemonTests.Arguments",
            ["arguments"] = new JsonArray(arguments.Select(argument => JsonValue.Create(argument)).ToArray())
        });
        Assert.That(response["result"]!["isError"]?.GetValue<bool>(), Is.Not.True, response.ToJsonString());
        var result = ToolResult(response);
        var backend = JsonNode.Parse(result["output"]!.GetValue<string>())!;
        using (Assert.EnterMultipleScope()) {
            Assert.That(result["exitStatus"]!.GetValue<int>(), Is.EqualTo(7));
            Assert.That(result["errorOutput"]!.GetValue<string>(), Is.EqualTo("daemon-test stderr"));
            Assert.That(backend["method"]!.GetValue<string>(), Is.EqualTo("DaemonTests.Arguments"));
            Assert.That(backend["arguments"]!.Deserialize<string[]>(), Is.EqualTo(arguments));
        }
    }

    [Test]
    public async Task ReusesWorkerAndMountsOnlyProjectDirectories() {
        await ExecuteMethodAsync("DaemonTests.First");
        string workerBefore = await SingleWorkerAsync();
        await ExecuteMethodAsync("DaemonTests.Reuse");
        string workerAfter = await SingleWorkerAsync();
        Assert.That(workerAfter, Is.EqualTo(workerBefore), "The retained worker was not reused.");

        var mounts = JsonNode.Parse((await DockerCheckedAsync(["inspect", "--format", "{{json .Mounts}}", workerBefore])).standardOutput)!.AsArray();
        string fingerprint = (await DockerCheckedAsync([
            "inspect", "--format", "{{index .Config.Labels \"net.slothsoft.compose-unity.worker-configuration\"}}", workerBefore
        ])).standardOutput.Trim();
        string[] destinations = mounts.Select(mount => mount!["Destination"]!.GetValue<string>()).ToArray();
        int projectMounts = destinations.Count(destination => ProjectDirectoryRegex().IsMatch(destination));
        int dockerMounts = destinations.Count(destination => destination.Equals("/var/run/docker.sock", StringComparison.Ordinal)
                                                               || destination.Equals(@"\\.\pipe\docker_engine", StringComparison.OrdinalIgnoreCase));
        using (Assert.EnterMultipleScope()) {
            Assert.That(projectMounts, Is.EqualTo(3));
            Assert.That(dockerMounts, Is.Zero, "The worker must not receive the controller's Docker endpoint.");
            Assert.That(fingerprint, Does.Match("^[0-9a-f]{64}$"));
        }
    }

    [Test]
    public async Task ParsesTestResultsFromWorker() {
        var response = await CallToolAsync("run_tests", new JsonObject {
            ["projectRoot"] = project,
            ["modes"] = new JsonArray("EditMode", "Play Mode")
        });
        Assert.That(response["result"]!["isError"]?.GetValue<bool>(), Is.Not.True, response.ToJsonString());
        var result = ToolResult(response);
        using (Assert.EnterMultipleScope()) {
            Assert.That(result["outcome"]!.GetValue<string>(), Is.EqualTo("passed"));
            Assert.That(result["counts"]!["total"]!.GetValue<int>(), Is.EqualTo(2));
            Assert.That(result["counts"]!["passed"]!.GetValue<int>(), Is.EqualTo(2));
        }
    }

    async Task PublishAsync(string projectFile, string runtime, string output) {
        var result = await RunAsync("dotnet", [
            "publish", projectFile, "--nologo", "--configuration", "Release", "--runtime", runtime,
            "--self-contained", "true", "--output", output
        ], TimeSpan.FromMinutes(3));
        Assert.That(result.exitCode, Is.Zero, () => $"dotnet publish failed.\nstdout:\n{result.standardOutput}\nstderr:\n{result.standardError}");
    }

    async Task WaitForHealthyControllerAsync() {
        var deadline = DateTime.UtcNow.AddMinutes(2);
        string health = string.Empty;
        while (DateTime.UtcNow < deadline) {
            var result = await DockerCheckedAsync(["inspect", "--format", "{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}", container]);
            health = result.standardOutput.Trim();
            if (health == "healthy") {
                return;
            }
            if (health is "unhealthy" or "exited" or "dead") {
                throw new InvalidOperationException($"Daemon-test controller became {health}.\n{await ControllerLogsAsync()}");
            }
            await Task.Delay(250);
        }

        throw new TimeoutException($"Daemon-test controller did not become healthy within two minutes (last state: {health}).\n{await ControllerLogsAsync()}");
    }

    async Task ConfigureMcpClientAsync() {
        httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (expectedOs == "windows") {
            string address = (await DockerCheckedAsync(["inspect", "--format", "{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}", container])).standardOutput.Trim();
            Assert.That(IpAddressRegex().IsMatch(address), Is.True, $"Could not determine the Windows container address: {address}");
            endpoint = new Uri($"http://{address}:8080/mcp");
            httpClient.DefaultRequestHeaders.Host = "localhost";
        } else {
            string published = (await DockerCheckedAsync(["port", container, "8080/tcp"])).standardOutput.Trim();
            var match = PublishedPortRegex().Match(published);
            Assert.That(match.Success, Is.True, $"Could not parse the published MCP port: {published}");
            endpoint = new Uri($"http://127.0.0.1:{match.Groups["port"].Value}/mcp");
        }
    }

    async Task InitializeMcpAsync() {
        var response = await InvokeMcpAsync("initialize", new JsonObject {
            ["protocolVersion"] = "2025-06-18",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "compose-unity-daemon-tests", ["version"] = "1" }
        });
        Assert.That(response["result"]!["serverInfo"]!["name"]!.GetValue<string>(), Is.EqualTo("compose-unity"));
    }

    async Task<JsonNode> CallToolAsync(string name, JsonObject arguments) =>
        await InvokeMcpAsync("tools/call", new JsonObject { ["name"] = name, ["arguments"] = arguments });

    async Task<JsonNode> InvokeMcpAsync(string method, JsonObject parameters) {
        var body = new JsonObject {
            ["jsonrpc"] = "2.0",
            ["id"] = Interlocked.Increment(ref requestId),
            ["method"] = method,
            ["params"] = parameters
        };
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await httpClient!.PostAsync(endpoint, content);
        string responseBody = await response.Content.ReadAsStringAsync();
        response.EnsureSuccessStatusCode();
        string[] data = responseBody.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("data: ", StringComparison.Ordinal))
            .ToArray();
        Assert.That(data, Has.Length.EqualTo(1), $"Unexpected MCP response: {responseBody}");
        return JsonNode.Parse(data[0][6..])!;
    }

    async Task ExecuteMethodAsync(string method) {
        var response = await CallToolAsync("execute_method", new JsonObject {
            ["projectRoot"] = project,
            ["method"] = method,
            ["arguments"] = new JsonArray()
        });
        Assert.That(response["result"]!["isError"]?.GetValue<bool>(), Is.Not.True, response.ToJsonString());
    }

    async Task<string> SingleWorkerAsync() {
        var result = await DockerCheckedAsync([
            "ps", "--all", "--quiet",
            "--filter", "label=net.slothsoft.compose-unity.kind=worker",
            "--filter", $"label=net.slothsoft.compose-unity.controller={controllerId}"
        ]);
        string[] workers = Lines(result.standardOutput);
        Assert.That(workers, Has.Length.EqualTo(1), $"Expected one retained worker, found {workers.Length}.");
        return workers[0];
    }

    async Task<ProcessResult> DockerCheckedAsync(IReadOnlyList<string> arguments, TimeSpan? timeout = null) {
        var result = await RunDockerAsync(arguments, timeout ?? TimeSpan.FromSeconds(30));
        AssertDockerSucceeded(result, string.Join(' ', arguments));
        return result;
    }

    Task<ProcessResult> RunDockerAsync(IReadOnlyList<string> arguments, TimeSpan timeout) =>
        RunAsync("docker", ["--context", dockerContext, .. arguments], timeout);

    async Task<string> ControllerLogsAsync() {
        var result = await RunDockerAsync(["logs", container], TimeSpan.FromSeconds(30));
        return result.standardOutput + result.standardError;
    }

    async Task CleanupAsync() {
        if (!string.IsNullOrWhiteSpace(controllerId)) {
            var owned = await RunDockerAsync(["ps", "--all", "--quiet", "--filter", $"label=net.slothsoft.compose-unity.controller={controllerId}"], TimeSpan.FromSeconds(30));
            foreach (string ownedContainer in Lines(owned.standardOutput)) {
                await RunDockerAsync(["rm", "--force", ownedContainer], TimeSpan.FromSeconds(30));
            }
        }
        if (controllerStarted) {
            await RunDockerAsync(["rm", "--force", container], TimeSpan.FromSeconds(30));
            controllerStarted = false;
        }
        if (imageBuilt) {
            await RunDockerAsync(["image", "rm", "--force", image], TimeSpan.FromSeconds(30));
            imageBuilt = false;
        }
        if (staging.Length > 0 && Directory.Exists(staging)) {
            string temporaryRoot = Path.GetFullPath(Path.GetTempPath());
            string fullStaging = Path.GetFullPath(staging);
            if (!fullStaging.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(fullStaging).StartsWith("compose-unity-daemon-tests-", StringComparison.Ordinal)) {
                throw new InvalidOperationException($"Refusing to remove unexpected staging directory: {fullStaging}");
            }
            Directory.Delete(fullStaging, true);
        }
    }

    static JsonNode ToolResult(JsonNode response) => response["result"]!["structuredContent"]!["result"]!;

    static bool IsLocalEndpoint(string value) => value.StartsWith("npipe://", StringComparison.OrdinalIgnoreCase)
                                                  || value.StartsWith("unix://", StringComparison.OrdinalIgnoreCase);

    static string WindowsDockerMount(string endpoint) {
        var match = WindowsPipeRegex().Match(endpoint);
        Assert.That(match.Success, Is.True, $"Windows daemon tests require a named-pipe context, but '{endpoint}' uses another endpoint.");
        return $@"type=npipe,source=\\.\pipe\{match.Groups["pipe"].Value},target=\\.\pipe\docker_engine";
    }

    static string FindRepository() {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "docker-compose-unity.sln"))) {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    static void AssertDockerSucceeded(ProcessResult result, string operation) =>
        Assert.That(result.exitCode, Is.Zero, () => $"Docker failed to {operation}.\nstdout:\n{result.standardOutput}\nstderr:\n{result.standardError}");

    static string Detail(ProcessResult result) {
        string value = string.IsNullOrWhiteSpace(result.standardError) ? result.standardOutput : result.standardError;
        value = value.Trim();
        return value.Length == 0 ? $"exit code {result.exitCode}" : value;
    }

    static string[] Lines(string value) => value.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    static async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout) {
        var startInfo = new ProcessStartInfo(executable) {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in arguments) {
            startInfo.ArgumentList.Add(argument);
        }

        try {
            using var process = Process.Start(startInfo);
            if (process is null) {
                return new ProcessResult(-1, string.Empty, $"Could not start {executable}.");
            }
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            using var cancellation = new CancellationTokenSource(timeout);
            try {
                await process.WaitForExitAsync(cancellation.Token);
            } catch (OperationCanceledException) {
                process.Kill(true);
                await process.WaitForExitAsync();
                return new ProcessResult(-1, await standardOutput, $"Timed out after {timeout}.\n{await standardError}");
            }
            return new ProcessResult(process.ExitCode, await standardOutput, await standardError);
        } catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or FileNotFoundException) {
            return new ProcessResult(-1, string.Empty, exception.Message);
        }
    }

    [GeneratedRegex(@"^npipe:/{4}\./pipe/(?<pipe>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex WindowsPipeRegex();

    [GeneratedRegex(@":(?<port>\d+)$")]
    private static partial Regex PublishedPortRegex();

    [GeneratedRegex(@"^\d{1,3}(\.\d{1,3}){3}$")]
    private static partial Regex IpAddressRegex();

    [GeneratedRegex(@"(?:^|[\\/])(Assets|Packages|ProjectSettings)$")]
    private static partial Regex ProjectDirectoryRegex();

    sealed record ProcessResult(int exitCode, string standardOutput, string standardError);
}
