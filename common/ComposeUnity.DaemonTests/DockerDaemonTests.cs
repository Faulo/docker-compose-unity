using System.Diagnostics;

namespace ComposeUnity.DaemonTests;

[Category("DockerDaemon")]
[TestFixture("linux", "linux")]
[TestFixture("windows", "windows")]
public sealed class DockerDaemonTests(string expectedOs, string defaultContext) {
    string repository = string.Empty;

    [OneTimeSetUp]
    public async Task DiscoverDaemonAsync() {
        repository = FindRepository();

        var inspection = await RunAsync("docker", ["context", "inspect", defaultContext, "--format", "{{.Endpoints.docker.Host}}"], TimeSpan.FromSeconds(5));
        if (inspection.exitCode != 0) {
            Assert.Ignore($"Docker context '{defaultContext}' is unavailable: {Detail(inspection)}");
        }

        string endpoint = inspection.standardOutput.Trim();
        if (!IsLocalEndpoint(endpoint)) {
            Assert.Ignore($"Docker context '{defaultContext}' is not local: {endpoint}");
        }

        var version = await RunAsync("docker", ["--context", defaultContext, "version", "--format", "{{.Server.Os}}"], TimeSpan.FromSeconds(5));
        if (version.exitCode != 0) {
            Assert.Ignore($"Docker context '{defaultContext}' is offline: {Detail(version)}");
        }

        Assert.That(version.standardOutput.Trim(), Is.EqualTo(expectedOs).IgnoreCase,
            $"Docker context '{defaultContext}' does not target the expected container OS.");
    }

    [Test]
    [CancelAfter(10 * 60 * 1000)]
    public async Task RunsComposeUnityControllerAgainstRealDaemon() {
        string script = Path.Combine(repository, "common", "ComposeUnity.DaemonTests", "daemon-tests.ps1");
        var result = await RunAsync(
            "pwsh",
            ["-NoLogo", "-NoProfile", "-File", script, "-Context", defaultContext, "-ExpectedOs", expectedOs],
            TimeSpan.FromMinutes(9));

        Assert.That(result.exitCode, Is.Zero, () => $"Daemon harness failed.\nstdout:\n{result.standardOutput}\nstderr:\n{result.standardError}");
        Assert.That(result.standardOutput, Does.Contain($"Daemon tests passed for Docker context '{defaultContext}'."));
    }

    static bool IsLocalEndpoint(string endpoint) =>
        endpoint.StartsWith("npipe://", StringComparison.OrdinalIgnoreCase)
        || endpoint.StartsWith("unix://", StringComparison.OrdinalIgnoreCase);

    static string FindRepository() {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "docker-compose-unity.sln"))) {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    static string Detail(ProcessResult result) {
        string value = string.IsNullOrWhiteSpace(result.standardError) ? result.standardOutput : result.standardError;
        value = value.Trim();
        return value.Length == 0 ? $"exit code {result.exitCode}" : value;
    }

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

            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
            Task<string> standardError = process.StandardError.ReadToEndAsync();
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

    sealed record ProcessResult(int exitCode, string standardOutput, string standardError);
}
