using System.Diagnostics;
using System.Security;
using System.Text.Json;

if (args.Contains("--version", StringComparer.Ordinal)) {
    Console.WriteLine("ComposeUnity daemon-test backend 1.0");
    return 0;
}

if (args.Length == 3 && args[0] == "sidecar" && args[1] == "probe-project") {
    return runControllerProbe(args[2]);
}

int separator = Array.IndexOf(args, "--");
if (separator < 0 || separator + 1 >= args.Length) {
    Console.Error.WriteLine($"Unexpected daemon-test command: {JsonSerializer.Serialize(args)}");
    return 2;
}

string[] command = args[(separator + 1)..];
return command[0] switch {
    "method" => executeMethod(command),
    "tests" => runTests(command),
    _ => unexpected(command)
};

static int runControllerProbe(string root) {
    string executable = Path.Combine(AppContext.BaseDirectory,
        OperatingSystem.IsWindows() ? "compose-unity-controller.exe" : "compose-unity-controller");
    var startInfo = new ProcessStartInfo(executable) { UseShellExecute = false };
    startInfo.ArgumentList.Add("sidecar");
    startInfo.ArgumentList.Add("probe-project");
    startInfo.ArgumentList.Add(root);
    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the real project probe.");
    process.WaitForExit();
    return process.ExitCode;
}

static int executeMethod(string[] command) {
    if (command.Length < 4) {
        Console.Error.WriteLine("Invalid method command.");
        return 2;
    }

    int arguments = Array.IndexOf(command, "--");
    Console.WriteLine(JsonSerializer.Serialize(new {
        method = command[2],
        arguments = arguments < 0 ? Array.Empty<string>() : command[(arguments + 1)..]
    }));
    Console.Error.WriteLine("daemon-test stderr");
    return 7;
}

static int runTests(string[] command) {
    int project = Array.IndexOf(command, "-") + 1;
    string[] modes = project > 0 && project + 1 < command.Length ? command[(project + 1)..] : [];
    string cases = string.Concat(modes.Select(mode => $"<testcase name=\"{SecurityElement.Escape(mode)}\" classname=\"DaemonTests\" />"));
    Console.WriteLine($"<testsuites><testsuite tests=\"{modes.Length}\" failures=\"0\" errors=\"0\" skipped=\"0\" time=\"0.01\">{cases}</testsuite></testsuites>");
    return 0;
}

static int unexpected(string[] command) {
    Console.Error.WriteLine($"Unexpected daemon-test operation: {JsonSerializer.Serialize(command)}");
    return 2;
}
