using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;

internal static class Program
{
    private const string SteamDirectory = @"C:\steam";
    private const string InstallerPath = @"C:\steamcmd\steamcmd-installer.exe";
    private static readonly string SteamCmdPath = Path.Combine(SteamDirectory, "steamcmd.exe");
    private static readonly string LockPath = Path.Combine(SteamDirectory, ".steamcmd.lock");

    public static int Main(string[] args)
    {
        try
        {
            Directory.CreateDirectory(SteamDirectory);
            using var invocationLock = AcquireInvocationLock();
            var installed = EnsureSteamCmdInstalled();

            ConsoleCancelEventHandler keepWrapperAlive = (_, eventArgs) => eventArgs.Cancel = true;
            Console.CancelKeyPress += keepWrapperAlive;
            try
            {
                if (installed)
                {
                    RunSteamCmd(["+quit"]);
                }
                return RunSteamCmd(args);
            }
            finally
            {
                Console.CancelKeyPress -= keepWrapperAlive;
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"steamcmd: {exception.Message}");
            return 1;
        }
    }

    private static int RunSteamCmd(IEnumerable<string> args)
    {
        var startInfo = new ProcessStartInfo(SteamCmdPath)
        {
            UseShellExecute = false,
            WorkingDirectory = Environment.CurrentDirectory
        };
        foreach (var argument in args)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the SteamCMD installation.");
        process.WaitForExit();
        var exitCode = process.ExitCode;
        WaitForSteamCmdChildren();
        return exitCode;
    }

    private static void WaitForSteamCmdChildren()
    {
        var quietChecks = 0;
        while (quietChecks < 2)
        {
            var childFound = false;
            foreach (var process in Process.GetProcessesByName("steamcmd"))
            {
                using (process)
                {
                    try
                    {
                        if (process.Id != Environment.ProcessId
                            && !process.HasExited
                            && process.MainModule?.FileName.Equals(SteamCmdPath, StringComparison.OrdinalIgnoreCase) == true)
                        {
                            childFound = true;
                        }
                    }
                    catch (InvalidOperationException)
                    {
                    }
                    catch (Win32Exception)
                    {
                    }
                }
            }
            quietChecks = childFound ? 0 : quietChecks + 1;
            Thread.Sleep(250);
        }
    }

    private static FileStream AcquireInvocationLock()
    {
        while (true)
        {
            try
            {
                return new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                Thread.Sleep(250);
            }
        }
    }

    private static bool EnsureSteamCmdInstalled()
    {
        if (IsExecutable(SteamCmdPath))
        {
            return false;
        }
        if (!IsExecutable(InstallerPath))
        {
            throw new InvalidDataException($"SteamCMD installer is missing or invalid: {InstallerPath}");
        }

        if (File.Exists(SteamCmdPath))
        {
            File.Delete(SteamCmdPath);
        }

        var temporaryPath = Path.Combine(SteamDirectory, $".steamcmd-{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(InstallerPath, temporaryPath);
            File.Move(temporaryPath, SteamCmdPath);
            return true;
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static bool IsExecutable(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return stream.Length >= 1024 && stream.ReadByte() == 'M' && stream.ReadByte() == 'Z';
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
