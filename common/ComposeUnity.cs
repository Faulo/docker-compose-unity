using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

internal static class Program
{
    private const long DefaultTimeoutSeconds = 86_400;
    private static readonly TimeSpan TerminationGracePeriod = TimeSpan.FromSeconds(10);

    public static async Task<int> Main(string[] args)
    {
        var executable = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "compose-unity");
        try
        {
            return executable.Equals("compose-unity-sidecar", StringComparison.OrdinalIgnoreCase)
                ? await RunSidecarCommandAsync(args)
                : await RunComposeUnityAsync(args);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"{executable}: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> RunSidecarCommandAsync(string[] args)
    {
        if (args.Length == 0)
        {
            return await RunSupervisorAsync();
        }

        if (args.Length == 1 && args[0].Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            return PrintStatus();
        }

        if (args.Length == 1 && args[0].Equals("health", StringComparison.OrdinalIgnoreCase))
        {
            return await CheckHealthAsync() ? 0 : 1;
        }

        if (args.Length == 2 && args[0].Equals("probe-project", StringComparison.Ordinal))
        {
            return ProjectProbe.Run(args[1]);
        }

        Console.Error.WriteLine("Usage: compose-unity-sidecar [status|health]");
        return 2;
    }

    private static async Task<int> RunComposeUnityAsync(string[] args)
    {
        var timeoutSeconds = ParseTimeout();
        var store = new StateStore();
        store.EnsureDirectories();

        var invocationId = Guid.NewGuid().ToString("N")[..12];
        var startedAt = DateTimeOffset.UtcNow;
        var deadline = ResolveDeadline(startedAt, timeoutSeconds);
        var command = SanitizeCommand(args);
        var workingDirectory = SanitizeText(Environment.CurrentDirectory, 512);
        ChildProcess? child = null;
        InvocationRecord? record = null;

        using var cancellation = new CancellationTokenSource();
        using var signals = SignalHandlers.Register(cancellation);

        try
        {
            child = ProcessTree.StartComposer(args, invocationId);
            record = new InvocationRecord
            {
                Id = invocationId,
                Command = command,
                WorkingDirectory = workingDirectory,
                StartedAtUtc = startedAt,
                TimeoutSeconds = timeoutSeconds,
                DeadlineUtc = deadline,
                Launcher = ProcessIdentity.Current(),
                RootProcess = ProcessIdentity.FromProcess(child.Process),
                ProcessGroupId = child.ProcessGroupId,
                JobName = child.JobName
            };
            store.WriteActive(record);
            store.WriteEvent(LifecycleEvent.Start(record));
            child.Resume();

            var processExit = child.Process.WaitForExitAsync();
            var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
            Task timeoutTask = timeoutSeconds == 0
                ? Task.Delay(Timeout.InfiniteTimeSpan)
                : WaitForTimeoutAsync(timeoutSeconds);
            var completed = await Task.WhenAny(processExit, cancellationTask, timeoutTask);

            if (completed == timeoutTask)
            {
                store.WriteEvent(LifecycleEvent.Finish("TIMEOUT", record, 124));
                await child.TerminateAsync(TerminationGracePeriod);
                return 124;
            }

            if (completed == cancellationTask)
            {
                store.WriteEvent(LifecycleEvent.Finish("CANCELLED", record, 130));
                await child.TerminateAsync(TerminationGracePeriod);
                return 130;
            }

            await processExit;
            var exitCode = child.Process.ExitCode;
            store.WriteEvent(LifecycleEvent.Finish(exitCode == 0 ? "END" : "FAILED", record, exitCode));
            return exitCode;
        }
        catch (Exception exception)
        {
            if (record is not null)
            {
                store.WriteEvent(LifecycleEvent.Finish("FAILED", record, 1, exception.Message));
            }
            else
            {
                store.WriteEvent(new LifecycleEvent
                {
                    Kind = "FAILED",
                    Id = invocationId,
                    Command = command,
                    WorkingDirectory = workingDirectory,
                    StartedAtUtc = startedAt,
                    FinishedAtUtc = DateTimeOffset.UtcNow,
                    ExitCode = 1,
                    Message = SanitizeText(exception.Message, 160)
                });
            }

            Console.Error.WriteLine($"compose-unity: {exception.Message}");
            return 1;
        }
        finally
        {
            if (record is not null)
            {
                store.RemoveActive(record.Id);
            }

            child?.Dispose();
        }
    }

    private static async Task<int> RunSupervisorAsync()
    {
        var mcpEnabled = McpActivation.Parse();
        var store = new StateStore();
        store.EnsureDirectories();

        foreach (var record in store.ReadActive())
        {
            await ReconcileOrphanAsync(store, record);
        }

        if (!store.CanWrite() || !await ProbeComposerAsync())
        {
            Console.Error.WriteLine("compose-unity-sidecar: readiness prerequisites failed");
            return 1;
        }

        using var cancellation = new CancellationTokenSource();
        using var signals = SignalHandlers.Register(cancellation);
        McpServerRuntime? mcp = null;
        var lastReconciliation = DateTimeOffset.MinValue;
        var exitCode = 0;

        try
        {
            if (mcpEnabled)
            {
                mcp = await McpServerRuntime.StartAsync(cancellation.Token);
            }

            var ready = new ReadyRecord
            {
                Supervisor = ProcessIdentity.Current(),
                StartedAtUtc = DateTimeOffset.UtcNow,
                McpEnabled = mcpEnabled,
                McpReady = mcp is not null
            };
            store.WriteReady(ready);
            Console.WriteLine($"READY os={RuntimeInformation.OSDescription.Replace(' ', '_')} compose-unity={await ComposerVersionAsync()} mcp={(mcpEnabled ? "http://0.0.0.0:8080/mcp" : "disabled")}");

            while (!cancellation.IsCancellationRequested)
            {
                if (mcp is not null && mcp.Completion.IsCompleted)
                {
                    Console.Error.WriteLine("compose-unity-sidecar: MCP server stopped unexpectedly");
                    exitCode = 1;
                    cancellation.Cancel();
                    break;
                }

                store.DrainEvents(WriteLifecycleEvent);
                if (DateTimeOffset.UtcNow - lastReconciliation >= TimeSpan.FromSeconds(2))
                {
                    foreach (var record in store.ReadActive())
                    {
                        await ReconcileOrphanAsync(store, record);
                    }
                    lastReconciliation = DateTimeOffset.UtcNow;
                }

                await Task.Delay(200, cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (mcp is not null)
            {
                await mcp.StopAsync();
            }

            foreach (var record in store.ReadActive())
            {
                if (record.RootProcess.IsAlive())
                {
                    WriteLifecycleEvent(LifecycleEvent.Finish("CANCELLED", record, 130, "sidecar stopping"));
                    await ProcessTree.TerminateRecordAsync(record, TerminationGracePeriod);
                }
                store.RemoveActive(record.Id);
            }

            store.RemoveReady();
            store.DrainEvents(WriteLifecycleEvent);
            if (mcp is not null)
            {
                await mcp.DisposeAsync();
            }
        }

        return exitCode;
    }

    private static int PrintStatus()
    {
        var store = new StateStore();
        var ready = store.ReadReady();
        var healthy = ready is not null
            && ready.Supervisor.IsAlive()
            && store.CanWrite()
            && (!ready.McpEnabled || ready.McpReady);
        var active = store.ReadActive()
            .Where(record => record.Launcher.IsAlive() || record.RootProcess.IsAlive())
            .OrderBy(record => record.StartedAtUtc)
            .ToList();

        Console.WriteLine(healthy ? "healthy" : "unhealthy");
        Console.WriteLine($"mcp: {(ready?.McpEnabled == true ? (ready.McpReady ? "ready" : "unready") : "disabled")}");
        Console.WriteLine($"active calls: {active.Count}");
        foreach (var record in active)
        {
            var elapsed = DateTimeOffset.UtcNow - record.StartedAtUtc;
            var timeout = record.TimeoutSeconds == 0 ? "disabled" : FormatDuration(TimeSpan.FromSeconds(record.TimeoutSeconds));
            Console.WriteLine($"{record.Id} {record.Command} pid={record.RootProcess.Pid} elapsed={FormatDuration(elapsed)} timeout={timeout} cwd={record.WorkingDirectory}");
        }

        return healthy ? 0 : 1;
    }

    private static async Task<bool> CheckHealthAsync()
    {
        try
        {
            var store = new StateStore();
            var ready = store.ReadReady();
            return ready is not null
                && ready.Supervisor.IsAlive()
                && store.CanWrite()
                && await ProbeComposerAsync()
                && (!ready.McpEnabled || (ready.McpReady && await McpServerRuntime.CheckHealthAsync()));
        }
        catch
        {
            return false;
        }
    }

    private static async Task ReconcileOrphanAsync(StateStore store, InvocationRecord record)
    {
        if (record.Launcher.IsAlive())
        {
            return;
        }

        WriteLifecycleEvent(LifecycleEvent.Finish("ORPHANED", record, null, "launcher disappeared"));
        if (record.RootProcess.IsAlive())
        {
            await ProcessTree.TerminateRecordAsync(record, TerminationGracePeriod);
        }
        store.RemoveActive(record.Id);
    }

    private static void WriteLifecycleEvent(LifecycleEvent lifecycleEvent)
    {
        var duration = lifecycleEvent.FinishedAtUtc.HasValue
            ? $" duration={FormatDuration(lifecycleEvent.FinishedAtUtc.Value - lifecycleEvent.StartedAtUtc)}"
            : string.Empty;
        var exit = lifecycleEvent.ExitCode.HasValue ? $" exit={lifecycleEvent.ExitCode.Value}" : string.Empty;
        var timeout = lifecycleEvent.TimeoutSeconds.HasValue
            ? $" timeout={(lifecycleEvent.TimeoutSeconds.Value == 0 ? "disabled" : lifecycleEvent.TimeoutSeconds.Value + "s")}"
            : string.Empty;
        var message = string.IsNullOrEmpty(lifecycleEvent.Message) ? string.Empty : $" message={SanitizeText(lifecycleEvent.Message, 160)}";
        Console.WriteLine($"{lifecycleEvent.Kind} id={lifecycleEvent.Id} pid={lifecycleEvent.Pid} command={lifecycleEvent.Command} cwd={lifecycleEvent.WorkingDirectory}{timeout}{exit}{duration}{message}");
    }

    private static long ParseTimeout()
    {
        var value = Environment.GetEnvironmentVariable("COMPOSE_UNITY_CALL_TIMEOUT");
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultTimeoutSeconds;
        }

        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || seconds < 0
            || seconds > (long)TimeSpan.MaxValue.TotalSeconds)
        {
            throw new ArgumentException($"Invalid COMPOSE_UNITY_CALL_TIMEOUT: {SanitizeText(value, 80)}");
        }

        return seconds;
    }

    private static async Task WaitForTimeoutAsync(long timeoutSeconds)
    {
        var remaining = TimeSpan.FromSeconds(timeoutSeconds);
        var maximumDelay = TimeSpan.FromDays(30);
        while (remaining > TimeSpan.Zero)
        {
            var delay = remaining < maximumDelay ? remaining : maximumDelay;
            await Task.Delay(delay);
            remaining -= delay;
        }
    }

    private static DateTimeOffset? ResolveDeadline(DateTimeOffset startedAt, long timeoutSeconds)
    {
        if (timeoutSeconds == 0)
        {
            return null;
        }
        try
        {
            return startedAt.AddSeconds(timeoutSeconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new ArgumentException("COMPOSE_UNITY_CALL_TIMEOUT exceeds the supported deadline range.");
        }
    }

    private static string SanitizeCommand(string[] args)
    {
        var index = Array.FindIndex(args, argument => argument.Equals("exec", StringComparison.OrdinalIgnoreCase));
        var commandIndex = index + 1;
        while (commandIndex > 0 && commandIndex < args.Length && args[commandIndex] == "--")
        {
            commandIndex++;
        }
        var value = index >= 0 && commandIndex < args.Length ? args[commandIndex] : "composer";
        var sanitized = new string(value.Take(64)
            .Select(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' ? character : '_')
            .ToArray());
        return string.IsNullOrEmpty(sanitized) ? "unknown" : sanitized;
    }

    private static string SanitizeText(string? value, int maximumLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "-";
        }

        var normalized = value.Replace('\r', '_').Replace('\n', '_').Replace('\t', '_');
        return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength];
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }
        return $"{(long)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
    }

    private static ProcessStartInfo ComposerStartInfo(bool redirectOutput)
    {
        ProcessStartInfo startInfo;
        if (OperatingSystem.IsWindows())
        {
            startInfo = new ProcessStartInfo("php.exe");
            startInfo.ArgumentList.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ComposerSetup", "bin", "composer.phar"));
            startInfo.ArgumentList.Add("-d");
            startInfo.ArgumentList.Add(@"C:\unity");
        }
        else
        {
            startInfo = new ProcessStartInfo("composer");
            startInfo.ArgumentList.Add("-d");
            startInfo.ArgumentList.Add("/var/unity");
        }

        startInfo.WorkingDirectory = Environment.CurrentDirectory;
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = redirectOutput;
        startInfo.RedirectStandardError = redirectOutput;
        startInfo.CreateNoWindow = redirectOutput;
        return startInfo;
    }

    internal static ProcessStartInfo ComposerStartInfo(string[] args, bool redirectOutput = false)
    {
        var startInfo = ComposerStartInfo(redirectOutput);
        foreach (var argument in args)
        {
            startInfo.ArgumentList.Add(argument);
        }
        return startInfo;
    }

    private static async Task<bool> ProbeComposerAsync()
    {
        try
        {
            using var process = Process.Start(ComposerStartInfo(["--version"], true));
            if (process is null)
            {
                return false;
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> ComposerVersionAsync()
    {
        try
        {
            using var process = Process.Start(ComposerStartInfo(["--version"], true));
            if (process is null)
            {
                return "unknown";
            }
            var output = await process.StandardOutput.ReadLineAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(timeout.Token);
            return SanitizeText(output, 80).Replace(' ', '_');
        }
        catch
        {
            return "unknown";
        }
    }
}

internal sealed class StateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly string root;
    private readonly string activeDirectory;
    private readonly string eventDirectory;
    private readonly string readyPath;

    internal StateStore()
    {
        root = Environment.GetEnvironmentVariable("COMPOSE_UNITY_STATE_DIRECTORY")
            ?? (OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "compose-unity")
                : "/run/compose-unity");
        activeDirectory = Path.Combine(root, "active");
        eventDirectory = Path.Combine(root, "events");
        readyPath = Path.Combine(root, "ready.json");
    }

    internal void EnsureDirectories()
    {
        Directory.CreateDirectory(activeDirectory);
        Directory.CreateDirectory(eventDirectory);
    }

    internal bool CanWrite()
    {
        try
        {
            EnsureDirectories();
            var probe = Path.Combine(root, $".write-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal void WriteActive(InvocationRecord record) => WriteAtomic(Path.Combine(activeDirectory, record.Id + ".json"), record);
    internal void RemoveActive(string id) => DeleteIfExists(Path.Combine(activeDirectory, id + ".json"));
    internal void WriteReady(ReadyRecord record) => WriteAtomic(readyPath, record);
    internal void RemoveReady() => DeleteIfExists(readyPath);

    internal ReadyRecord? ReadReady() => Read<ReadyRecord>(readyPath);

    internal IReadOnlyList<InvocationRecord> ReadActive()
    {
        try
        {
            EnsureDirectories();
            return Directory.EnumerateFiles(activeDirectory, "*.json")
                .Select(Read<InvocationRecord>)
                .Where(record => record is not null)
                .Cast<InvocationRecord>()
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    internal void WriteEvent(LifecycleEvent lifecycleEvent)
    {
        EnsureDirectories();
        var name = $"{DateTimeOffset.UtcNow.UtcTicks:D19}-{Guid.NewGuid():N}.json";
        WriteAtomic(Path.Combine(eventDirectory, name), lifecycleEvent);
    }

    internal void DrainEvents(Action<LifecycleEvent> consumer)
    {
        try
        {
            EnsureDirectories();
            foreach (var path in Directory.EnumerateFiles(eventDirectory, "*.json").OrderBy(path => path, StringComparer.Ordinal))
            {
                var lifecycleEvent = Read<LifecycleEvent>(path);
                if (lifecycleEvent is not null)
                {
                    consumer(lifecycleEvent);
                }
                DeleteIfExists(path);
            }
        }
        catch (IOException)
        {
        }
    }

    private static T? Read<T>(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            return default;
        }
    }

    private static void WriteAtomic<T>(string path, T value)
    {
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(temporaryPath, path, true);
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (FileNotFoundException)
        {
        }
    }
}

internal sealed class InvocationRecord
{
    public string Id { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public DateTimeOffset StartedAtUtc { get; set; }
    public long TimeoutSeconds { get; set; }
    public DateTimeOffset? DeadlineUtc { get; set; }
    public ProcessIdentity Launcher { get; set; } = new();
    public ProcessIdentity RootProcess { get; set; } = new();
    public int ProcessGroupId { get; set; }
    public string? JobName { get; set; }
}

internal sealed class ReadyRecord
{
    public ProcessIdentity Supervisor { get; set; } = new();
    public DateTimeOffset StartedAtUtc { get; set; }
    public bool McpEnabled { get; set; }
    public bool McpReady { get; set; }
}

internal sealed class ProcessIdentity
{
    public int Pid { get; set; }
    public long StartMarker { get; set; }

    internal static ProcessIdentity Current() => FromProcess(Process.GetCurrentProcess());

    internal static ProcessIdentity FromProcess(Process process) => new()
    {
        Pid = process.Id,
        StartMarker = ReadStartMarker(process)
    };

    internal bool IsAlive()
    {
        if (Pid <= 0 || StartMarker <= 0)
        {
            return false;
        }
        try
        {
            using var process = Process.GetProcessById(Pid);
            return !process.HasExited && ReadStartMarker(process) == StartMarker;
        }
        catch
        {
            return false;
        }
    }

    private static long ReadStartMarker(Process process)
    {
        if (OperatingSystem.IsWindows())
        {
            return process.StartTime.ToUniversalTime().Ticks;
        }

        var stat = File.ReadAllText($"/proc/{process.Id}/stat");
        var commandEnd = stat.LastIndexOf(')');
        if (commandEnd < 0)
        {
            throw new InvalidDataException($"Invalid process stat for PID {process.Id}.");
        }
        var fields = stat[(commandEnd + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return long.Parse(fields[19], CultureInfo.InvariantCulture);
    }
}

internal sealed class LifecycleEvent
{
    public string Kind { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public int Pid { get; set; }
    public string Command { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? FinishedAtUtc { get; set; }
    public long? TimeoutSeconds { get; set; }
    public int? ExitCode { get; set; }
    public string? Message { get; set; }

    internal static LifecycleEvent Start(InvocationRecord record) => new()
    {
        Kind = "START",
        Id = record.Id,
        Pid = record.RootProcess.Pid,
        Command = record.Command,
        WorkingDirectory = record.WorkingDirectory,
        StartedAtUtc = record.StartedAtUtc,
        TimeoutSeconds = record.TimeoutSeconds
    };

    internal static LifecycleEvent Finish(string kind, InvocationRecord record, int? exitCode, string? message = null) => new()
    {
        Kind = kind,
        Id = record.Id,
        Pid = record.RootProcess.Pid,
        Command = record.Command,
        WorkingDirectory = record.WorkingDirectory,
        StartedAtUtc = record.StartedAtUtc,
        FinishedAtUtc = DateTimeOffset.UtcNow,
        TimeoutSeconds = kind == "TIMEOUT" ? record.TimeoutSeconds : null,
        ExitCode = exitCode,
        Message = message
    };
}

internal sealed class ChildProcess : IDisposable
{
    private readonly Action resume;
    private readonly IDisposable? nativeResource;
    private bool resumed;

    internal ChildProcess(Process process, int processGroupId, string? jobName, Action resume, IDisposable? nativeResource)
    {
        Process = process;
        ProcessGroupId = processGroupId;
        JobName = jobName;
        this.resume = resume;
        this.nativeResource = nativeResource;
    }

    internal Process Process { get; }
    internal int ProcessGroupId { get; }
    internal string? JobName { get; }

    internal void Resume()
    {
        if (!resumed)
        {
            resume();
            resumed = true;
        }
    }

    internal Task TerminateAsync(TimeSpan gracePeriod) => ProcessTree.TerminateChildAsync(this, gracePeriod);

    public void Dispose()
    {
        Process.Dispose();
        nativeResource?.Dispose();
    }
}

internal static class ProcessTree
{
    internal static ChildProcess StartComposer(string[] args, string invocationId)
    {
        return OperatingSystem.IsWindows()
            ? WindowsProcessTree.Start(Program.ComposerStartInfo(args), invocationId)
            : UnixProcessTree.Start(Program.ComposerStartInfo(args));
    }

    internal static Task TerminateChildAsync(ChildProcess child, TimeSpan gracePeriod) => OperatingSystem.IsWindows()
        ? WindowsProcessTree.TerminateAsync(child.Process, child.JobName, gracePeriod)
        : UnixProcessTree.TerminateAsync(child.Process, child.ProcessGroupId, gracePeriod);

    internal static async Task TerminateRecordAsync(InvocationRecord record, TimeSpan gracePeriod)
    {
        if (!record.RootProcess.IsAlive())
        {
            return;
        }
        try
        {
            using var process = Process.GetProcessById(record.RootProcess.Pid);
            if (OperatingSystem.IsWindows())
            {
                await WindowsProcessTree.TerminateAsync(process, record.JobName, gracePeriod);
            }
            else
            {
                await UnixProcessTree.TerminateAsync(process, record.ProcessGroupId, gracePeriod);
            }
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }
}

internal static class UnixProcessTree
{
    private const int SigTerm = 15;
    private const int SigKill = 9;

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);

    internal static ChildProcess Start(ProcessStartInfo composer)
    {
        var startInfo = new ProcessStartInfo("/usr/bin/setsid") { UseShellExecute = false };
        startInfo.ArgumentList.Add("--wait");
        startInfo.ArgumentList.Add(composer.FileName);
        foreach (var argument in composer.ArgumentList)
        {
            startInfo.ArgumentList.Add(argument);
        }
        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start Composer process group.");
        return new ChildProcess(process, process.Id, null, () => { }, null);
    }

    internal static async Task TerminateAsync(Process? process, int processGroupId, TimeSpan gracePeriod)
    {
        if (processGroupId <= 0)
        {
            return;
        }
        kill(-processGroupId, SigTerm);
        if (process is not null && await WaitForExitAsync(process, gracePeriod))
        {
            return;
        }
        await Task.Delay(gracePeriod);
        kill(-processGroupId, SigKill);
        if (process is not null)
        {
            await WaitForExitAsync(process, TimeSpan.FromSeconds(2));
        }
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        if (process.HasExited)
        {
            return true;
        }
        var wait = process.WaitForExitAsync();
        return await Task.WhenAny(wait, Task.Delay(timeout)) == wait;
    }
}

internal static class WindowsProcessTree
{
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateNewProcessGroup = 0x00000200;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const uint JobObjectTerminate = 0x0008;
    private const uint JobObjectQuery = 0x0004;
    private const uint CtrlBreakEvent = 1;
    private const int StdInputHandle = -10;
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, uint informationClass, IntPtr information, uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(
        string? applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GenerateConsoleCtrlEvent(uint controlEvent, uint processGroupId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenJobObject(uint desiredAccess, bool inheritHandle, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int standardHandle);

    internal static ChildProcess Start(ProcessStartInfo startInfo, string invocationId)
    {
        var jobName = "compose-unity-" + invocationId;
        var job = CreateJobObject(IntPtr.Zero, jobName);
        if (job == IntPtr.Zero)
        {
            throw NativeError("Failed to create Windows Job Object");
        }

        try
        {
            ConfigureKillOnClose(job);
            var startupInfo = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfo>(),
                Flags = StartfUseStdHandles,
                StandardInput = GetStdHandle(StdInputHandle),
                StandardOutput = GetStdHandle(StdOutputHandle),
                StandardError = GetStdHandle(StdErrorHandle)
            };
            var commandLine = new StringBuilder(BuildCommandLine(startInfo));
            if (!CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, true,
                    CreateSuspended | CreateNewProcessGroup, IntPtr.Zero, startInfo.WorkingDirectory,
                    ref startupInfo, out var processInformation))
            {
                throw NativeError("Failed to create suspended Composer process");
            }

            if (!AssignProcessToJobObject(job, processInformation.Process))
            {
                CloseHandle(processInformation.Thread);
                CloseHandle(processInformation.Process);
                throw NativeError("Failed to assign Composer to Windows Job Object");
            }

            var process = Process.GetProcessById((int)processInformation.ProcessId);
            CloseHandle(processInformation.Process);
            var jobHandle = new NativeHandle(job);
            job = IntPtr.Zero;
            return new ChildProcess(process, 0, jobName, () =>
            {
                if (ResumeThread(processInformation.Thread) == uint.MaxValue)
                {
                    throw NativeError("Failed to resume Composer process");
                }
                CloseHandle(processInformation.Thread);
            }, jobHandle);
        }
        finally
        {
            if (job != IntPtr.Zero)
            {
                CloseHandle(job);
            }
        }
    }

    internal static async Task TerminateAsync(Process? process, string? jobName, TimeSpan gracePeriod)
    {
        if (string.IsNullOrWhiteSpace(jobName))
        {
            return;
        }
        var job = OpenJobObject(JobObjectTerminate | JobObjectQuery, false, jobName);
        if (job == IntPtr.Zero)
        {
            return;
        }
        try
        {
            if (process is not null && !process.HasExited)
            {
                GenerateConsoleCtrlEvent(CtrlBreakEvent, (uint)process.Id);
                if (await WaitForExitAsync(process, gracePeriod))
                {
                    return;
                }
            }
            TerminateJobObject(job, 124);
            if (process is not null)
            {
                await WaitForExitAsync(process, TimeSpan.FromSeconds(2));
            }
        }
        finally
        {
            CloseHandle(job);
        }
    }

    private static void ConfigureKillOnClose(IntPtr job)
    {
        var information = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose }
        };
        var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(information, pointer, false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, pointer, (uint)size))
            {
                throw NativeError("Failed to configure Windows Job Object");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static string BuildCommandLine(ProcessStartInfo startInfo)
    {
        var values = new List<string> { startInfo.FileName };
        values.AddRange(startInfo.ArgumentList);
        return string.Join(" ", values.Select(QuoteArgument));
    }

    private static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && !argument.Any(character => char.IsWhiteSpace(character) || character == '"'))
        {
            return argument;
        }
        var result = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
            }
            else if (character == '"')
            {
                result.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
            }
            else
            {
                result.Append('\\', backslashes).Append(character);
                backslashes = 0;
            }
        }
        result.Append('\\', backslashes * 2).Append('"');
        return result.ToString();
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        if (process.HasExited)
        {
            return true;
        }
        var wait = process.WaitForExitAsync();
        return await Task.WhenAny(wait, Task.Delay(timeout)) == wait;
    }

    private static Exception NativeError(string message) => new InvalidOperationException($"{message}: {Marshal.GetLastWin32Error()}");

    private sealed class NativeHandle(IntPtr handle) : IDisposable
    {
        public void Dispose() => CloseHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2;
        public IntPtr Reserved2Pointer;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}

internal static class SignalHandlers
{
    internal static IDisposable Register(CancellationTokenSource cancellation)
    {
        ConsoleCancelEventHandler consoleHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += consoleHandler;

        PosixSignalRegistration? terminate = null;
        PosixSignalRegistration? interrupt = null;
        if (!OperatingSystem.IsWindows())
        {
            terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                cancellation.Cancel();
            });
            interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context =>
            {
                context.Cancel = true;
                cancellation.Cancel();
            });
        }

        return new Registration(() =>
        {
            Console.CancelKeyPress -= consoleHandler;
            terminate?.Dispose();
            interrupt?.Dispose();
        });
    }

    private sealed class Registration(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
