using System;
using System.Diagnostics;
using System.IO;

const string WorkingDirectory = @"C:\unity";

var startInfo = new ProcessStartInfo("php.exe")
{
    WorkingDirectory = WorkingDirectory,
    UseShellExecute = false
};
startInfo.ArgumentList.Add(Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    "ComposerSetup",
    "bin",
    "composer.phar"
));
startInfo.ArgumentList.Add("-d");
startInfo.ArgumentList.Add(WorkingDirectory);
foreach (var argument in args)
{
    startInfo.ArgumentList.Add(argument);
}

using var process = Process.Start(startInfo)
    ?? throw new InvalidOperationException("Failed to start PHP.");
process.WaitForExit();
return process.ExitCode;
