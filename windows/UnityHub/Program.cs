using System;
using System.Diagnostics;
using System.IO;

const string realExecutableName = "Unity Hub.real.exe";

string installDirectory = AppContext.BaseDirectory;
var startInfo = new ProcessStartInfo(
    Path.Combine(installDirectory, realExecutableName)
) { WorkingDirectory = installDirectory, UseShellExecute = false };

int firstForwardedArgument = 0;
// Electron switches before Unity's delimiter hide --headless from Hub's yargs parser.
if (args.Length >= 2 && args[0] == "--" && args[1] == "--headless") {
    startInfo.ArgumentList.Add("--disable-gpu-sandbox");
    firstForwardedArgument = 1;
}

for (int index = firstForwardedArgument; index < args.Length; index++) {
    startInfo.ArgumentList.Add(args[index]);
}

using var process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("Failed to start Unity Hub.");
process.WaitForExit();
return process.ExitCode;