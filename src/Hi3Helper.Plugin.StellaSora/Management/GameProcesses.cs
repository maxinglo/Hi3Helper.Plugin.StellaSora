using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Hi3Helper.Plugin.StellaSora.Configuration;

namespace Hi3Helper.Plugin.StellaSora.Management;

internal static partial class GameProcesses
{
    internal static void EnsureIdle(string root, StellaSoraRegion region)
    {
        using Process? game = FindGame(root, region, true);
        if (game != null)
            throw new InvalidOperationException(
                "Close the game before installing, updating, repairing, or uninstalling it.");
        Process[] launchers = Process.GetProcessesByName(region.LauncherProcessName);
        try
        {
            if (launchers.Length > 0)
                throw new InvalidOperationException(
                    "Exit the official launcher before modifying game files to prevent concurrent writes.");
        }
        finally
        {
            foreach (Process launcher in launchers) launcher.Dispose();
        }
    }

    internal static Process? FindGame(string root, StellaSoraRegion region, bool requireAccess = false)
    {
        string expected = SafePaths.Resolve(root, region.Executable);
        Process? found = null;
        Process[] processes = Process.GetProcessesByName(region.ExecutableStem);
        foreach (Process process in processes)
        {
            try
            {
                if (!process.HasExited &&
                    string.Equals(GetImagePath(process.Id), expected, StringComparison.OrdinalIgnoreCase))
                {
                    found = process;
                    break;
                }
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                if (requireAccess)
                {
                    foreach (Process item in processes) item.Dispose();
                    throw new InvalidOperationException(
                        "Cannot inspect a game process. Close the game before modifying its files.", ex);
                }
            }
        }

        foreach (Process process in processes)
            if (found != process)
                process.Dispose();
        return found;
    }

    private static unsafe string GetImagePath(int processId)
    {
        using SafeProcessHandle handle = OpenProcess(0x1000, false, processId);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        char* buffer = stackalloc char[32768];
        int length = 32768;
        if (!QueryFullProcessImageName(handle, 0, buffer, ref length))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return new string(buffer, 0, length);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit,
        int processId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageName(SafeProcessHandle process, int flags, char* name,
        ref int length);
}
