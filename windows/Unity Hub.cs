using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

internal static class UnityHubLauncher
{
    private const string RealExecutableName = "Unity Hub.real.exe";

    public static int Main(string[] args)
    {
        var arguments = new List<string>(args);

        // Electron switches before Unity's delimiter hide --headless from Hub's yargs parser.
        if (arguments.Count >= 2 && arguments[0] == "--" && arguments[1] == "--headless")
        {
            arguments.RemoveAt(0);
            arguments.Insert(0, "--disable-gpu-sandbox");
        }

        var launcherPath = Assembly.GetExecutingAssembly().Location;
        var installDirectory = Path.GetDirectoryName(launcherPath);
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(installDirectory, RealExecutableName),
                Arguments = JoinArguments(arguments),
                WorkingDirectory = installDirectory,
                UseShellExecute = false
            }
        };

        process.Start();
        process.WaitForExit();
        return process.ExitCode;
    }

    private static string JoinArguments(IEnumerable<string> arguments)
    {
        var commandLine = new StringBuilder();
        foreach (var argument in arguments)
        {
            if (commandLine.Length > 0)
            {
                commandLine.Append(' ');
            }
            commandLine.Append(QuoteArgument(argument));
        }
        return commandLine.ToString();
    }

    private static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
        {
            return argument;
        }

        var quoted = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                quoted.Append('\\', backslashes * 2 + 1);
                quoted.Append(character);
                backslashes = 0;
                continue;
            }

            quoted.Append('\\', backslashes);
            quoted.Append(character);
            backslashes = 0;
        }

        quoted.Append('\\', backslashes * 2);
        quoted.Append('"');
        return quoted.ToString();
    }
}
