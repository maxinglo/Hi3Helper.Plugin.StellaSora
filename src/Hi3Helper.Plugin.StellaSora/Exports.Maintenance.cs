using System.Runtime.InteropServices;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.StellaSora.Configuration;
using Hi3Helper.Plugin.StellaSora.Management;

namespace Hi3Helper.Plugin.StellaSora;

public partial class Exports
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate int MaintenanceDelegate(char* gamePath, int gamePathLength, char* regionId,
        int regionIdLength,
        char* reportPath, int reportPathLength, nint progressCallback);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MaintenanceProgressDelegate(int phase, long current, long total);

    private static unsafe void RegisterMaintenanceExports()
    {
        TryRegisterApiExport<MaintenanceDelegate>("CheckStellaSoraGameIntegrity", CheckIntegrity);
        TryRegisterApiExport<MaintenanceDelegate>("RepairStellaSoraGame", RepairGame);
    }

    private static unsafe int CheckIntegrity(char* root, int rootLength, char* region, int regionLength,
        char* report, int reportLength, nint callback) => RunMaintenance(root, rootLength, region, regionLength, report,
        reportLength, callback, false);

    private static unsafe int RepairGame(char* root, int rootLength, char* region, int regionLength,
        char* report, int reportLength, nint callback) => RunMaintenance(root, rootLength, region, regionLength, report,
        reportLength, callback, true);

    private static unsafe int RunMaintenance(char* rootPointer, int rootLength, char* regionPointer, int regionLength,
        char* reportPointer, int reportLength, nint callbackPointer, bool repair)
    {
        try
        {
            if (rootPointer == null || reportPointer == null || regionPointer == null || rootLength <= 0 ||
                reportLength <= 0 || regionLength <= 0)
                throw new ArgumentException("A game path, region ID, and report path are required.");
            string root = Path.GetFullPath(new string(rootPointer, 0, rootLength));
            string report = Path.GetFullPath(new string(reportPointer, 0, reportLength));
            if (report.Equals(root, StringComparison.OrdinalIgnoreCase) || report.StartsWith(
                    root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The integrity report must be outside the game directory.");
            string id = new(regionPointer, 0, regionLength);
            StellaSoraRegion definition =
                StellaSoraRegion.Supported.SingleOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                ?? throw new NotSupportedException("This game region is not supported: " + id);
            MaintenanceProgressDelegate? callback = callbackPointer == 0
                ? null
                : Marshal.GetDelegateForFunctionPointer<MaintenanceProgressDelegate>(callbackPointer);
            using MaintenanceProgress operation = new(callback);
            return MaintainAsync(root, definition, report, repair, operation.Report, operation.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return unchecked((int)0x800704C7);
        }
        catch (Exception ex)
        {
            return Marshal.GetHRForException(ex);
        }
    }

    private sealed class MaintenanceProgress(MaintenanceProgressDelegate? callback) : IDisposable
    {
        private readonly CancellationTokenSource _cancel = new();
        internal CancellationToken Token => _cancel.Token;
        internal void Report(int phase, long current, long total)
        {
            if (callback != null && callback.Invoke(phase, current, total) != 0) _cancel.Cancel();
            Token.ThrowIfCancellationRequested();
        }
        public void Dispose() => _cancel.Dispose();
    }

    private static async Task<int> MaintainAsync(string root, StellaSoraRegion region, string reportPath, bool repair,
        Action<int, long, long> progress, CancellationToken token)
    {
        StellaSoraGameManager manager = new(region);
        StellaSoraGameInstaller installer = new(manager);
        manager.SetGamePath(root);
        List<string> issues = [];
        bool completed = false;
        string? error = null;
        try
        {
            (_, GameManifest manifest) = await manager.GetManifestAsync(token).ConfigureAwait(false);
            long totalSize = manifest.TotalSize;
            long processed = 0;
            foreach (GameFile file in manifest.Files)
            {
                token.ThrowIfCancellationRequested();
                if (!await Crc64.MatchesAsync(SafePaths.Resolve(root, file.Path), file, token).ConfigureAwait(false))
                    issues.Add(file.Path);
                processed += file.Size;
                progress(1, processed, totalSize);
            }

            if (repair)
            {
                await installer
                    .InstallAsync(
                        (in InstallProgress value) => progress(2, value.DownloadedBytes, value.TotalBytesToDownload),
                        null, token).ConfigureAwait(false);
                issues.Clear();
            }

            completed = true;
            return issues.Count == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            error = ex is OperationCanceledException ? "Cancelled" : ex.Message;
            throw;
        }
        finally
        {
            installer.Dispose();
            manager.Dispose();
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            using FileStream stream = new(reportPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            using System.Text.Json.Utf8JsonWriter writer = new(stream, new() { Indented = true });
            writer.WriteStartObject();
            writer.WriteString("Region", region.Id);
            writer.WriteString("GamePath", root);
            writer.WriteBoolean("Completed", completed);
            writer.WriteBoolean("Healthy", completed && issues.Count == 0);
            writer.WriteBoolean("RepairRequested", repair);
            writer.WriteString("Error", error);
            writer.WriteStartArray("Issues");
            foreach (string issue in issues) writer.WriteStringValue(issue);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
    }
}