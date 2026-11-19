using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Utility;
using Hi3Helper.Plugin.StellaSora.Management;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.Plugin.StellaSora;

public partial class Exports : SharedStaticV1Ext<Exports>
{
    static Exports()
    {
        Load<StellaSoraPlugin>(PluginVersion.Current);
        RegisterMaintenanceExports();
    }

    [UnmanagedCallersOnly(EntryPoint = "TryGetApiExport", CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int TryGetApiExport(char* name, void** result) => TryGetApiExportPointer(name, result);

    protected override (bool IsSupported, Task<bool> Task) LaunchGameFromGameManagerCoreAsync(
        GameManagerExtension.RunGameFromGameManagerContext context, string? arguments, bool boosted,
        ProcessPriorityClass priority, CancellationToken token)
        => (true, LaunchAsync(context, arguments, boosted, priority, token));

    private static async Task<bool> LaunchAsync(GameManagerExtension.RunGameFromGameManagerContext context,
        string? arguments, bool boosted, ProcessPriorityClass priority, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (context.GameManager is not StellaSoraGameManager manager)
            throw new InvalidOperationException("Unexpected game manager.");
        manager.LoadConfig();
        manager.IsGameInstalled(out bool installed);
        manager.GetGamePath(out string? root);
        if (!installed || root == null)
            throw new InvalidOperationException(
                "The game installation is incomplete. Install or repair it before launching.");
        using Process process = Process.Start(new ProcessStartInfo
        {
            FileName = SafePaths.Resolve(root, manager.Region.Executable),
            WorkingDirectory = root,
            Arguments = arguments ?? "",
            UseShellExecute = true
        }) ?? throw new IOException("Windows did not create a game process.");
        try
        {
            process.PriorityClass = priority;
            process.PriorityBoostEnabled = boosted;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            InstanceLogger.LogDebug(ex, "Cannot apply optional game process priority settings.");
        }

        await process.WaitForExitAsync(token).ConfigureAwait(false);
        return true;
    }

    protected override bool IsGameRunningCore(GameManagerExtension.RunGameFromGameManagerContext context,
        out bool running, out DateTime started)
    {
        using Process? process = Find(context);
        running = process != null;
        started = process?.StartTime ?? default;
        return true;
    }

    protected override (bool IsSupported, Task<bool> Task) WaitRunningGameCoreAsync(
        GameManagerExtension.RunGameFromGameManagerContext context, CancellationToken token)
        => (true, WaitAsync(context, token));

    private static async Task<bool> WaitAsync(GameManagerExtension.RunGameFromGameManagerContext context,
        CancellationToken token)
    {
        using Process? process = Find(context);
        if (process != null) await process.WaitForExitAsync(token).ConfigureAwait(false);
        return true;
    }

    protected override bool KillRunningGameCore(GameManagerExtension.RunGameFromGameManagerContext context,
        out bool running, out DateTime started)
    {
        using Process? process = Find(context);
        running = process != null;
        started = process?.StartTime ?? default;
        process?.Kill();
        return true;
    }

    private static Process? Find(GameManagerExtension.RunGameFromGameManagerContext context)
    {
        if (context.GameManager is not StellaSoraGameManager manager) return null;
        manager.GetGamePath(out string? root);
        return root == null ? null : GameProcesses.FindGame(root, manager.Region);
    }
}
