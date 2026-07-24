using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

internal static class ComposeUnityLauncher
{
    public static int Main(string[] args)
    {
        var composer = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ComposerSetup",
            "bin",
            "composer.phar"
        );
        var arguments = new List<string>
        {
            composer,
            "-d",
            @"C:\unity"
        };
        arguments.AddRange(args);

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "php.exe",
                Arguments = JoinArguments(arguments),
                WorkingDirectory = @"C:\unity",
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
