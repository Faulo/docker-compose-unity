using System.Diagnostics;
using System.Security;
using System.Text;
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
    "module-install" => installModules(command),
    "method" => executeMethod(command),
    "tests" => runTests(command),
    _ => unexpected(command)
};

static int installModules(string[] command) {
    if (command.Length != 3 || command[2] != "webgl") {
        Console.Error.WriteLine($"Invalid module-install command: {JsonSerializer.Serialize(command)}");
        return 2;
    }

    return 0;
}

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
    string[] values = arguments < 0 ? [] : command[(arguments + 1)..];
    if (command[2] == "Slothsoft.UnityExtensions.Editor.Build.WebGL") {
        if (values.Length != 1) {
            Console.Error.WriteLine("The WebGL build requires exactly one output path.");
            return 2;
        }

        string output = values[0];
        Directory.CreateDirectory(Path.Combine(output, "Build"));
        File.WriteAllText(Path.Combine(output, "index.html"), "<!doctype html><title>Daemon WebGL Build</title><h1>Ready</h1>");
        File.WriteAllBytes(Path.Combine(output, "Build", "game.data"), Encoding.ASCII.GetBytes("0123456789"));
        File.WriteAllBytes(Path.Combine(output, "Build", "game.wasm.br"), [1, 2, 3, 4]);
        File.WriteAllText(Path.Combine(output, "Build", "game.js.gz"), "compressed javascript fixture");
        return 0;
    }

    Console.WriteLine(JsonSerializer.Serialize(new {
        method = command[2],
        arguments = values
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
