using System.Security;
using System.Text.Json;
using System.Text.Json.Nodes;

if (args.Contains("--version", StringComparer.Ordinal)) {
    Console.WriteLine("ComposeUnity daemon-test backend 1.0");
    return 0;
}

if (args.Length == 3 && args[0] == "sidecar" && args[1] == "probe-project") {
    return probeProject(args[2]);
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

static int probeProject(string root) {
    try {
        foreach (string directory in new[] { "Assets", "Packages", "ProjectSettings" }) {
            if (!Directory.Exists(Path.Combine(root, directory))) {
                throw new InvalidOperationException($"Required Unity project directory is missing: {directory}");
            }
        }

        string[] version = File.ReadAllLines(Path.Combine(root, "ProjectSettings", "ProjectVersion.txt"));
        string[] settings = File.ReadAllLines(Path.Combine(root, "ProjectSettings", "ProjectSettings.asset"));
        var packages = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "Packages", "manifest.json")));
        var result = new {
            companyName = setting(settings, "companyName"),
            projectName = setting(settings, "productName"),
            projectVersion = setting(settings, "bundleVersion"),
            editorVersion = value(version, "m_EditorVersion:"),
            editorRevision = (string?)null,
            apiCompatibility = "DaemonTest",
            allowUnsafeCode = false,
            scriptingBackendOverrides = new Dictionary<string, string>(),
            renderPipeline = "DaemonTest",
            colorSpace = "DaemonTest",
            graphicsApis = new Dictionary<string, object>(),
            inputHandling = "DaemonTest",
            packages
        };
        Console.WriteLine(JsonSerializer.Serialize(result));
        return 0;
    } catch (Exception exception) {
        Console.Error.WriteLine(exception.Message);
        return 1;
    }
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

static string setting(IEnumerable<string> lines, string name) {
    string prefix = $"  {name}:";
    string value = lines.First(line => line.StartsWith(prefix, StringComparison.Ordinal))[prefix.Length..].Trim();
    if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') {
        return JsonSerializer.Deserialize<string>(value) ?? string.Empty;
    }

    return value.Length >= 2 && value[0] == '\'' && value[^1] == '\''
        ? value[1..^1].Replace("''", "'", StringComparison.Ordinal)
        : value;
}

static string value(IEnumerable<string> lines, string prefix) =>
    lines.First(line => line.StartsWith(prefix, StringComparison.Ordinal))[prefix.Length..].Trim();
