[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $GamePath,
    [ValidateSet('CN', 'EN', 'JP', 'KR', 'TW')][string] $Region = 'CN',
    [string] $LibraryPath = (Join-Path $PSScriptRoot 'StellaSora.dll'),
    [string] $ReportPath = (Join-Path $PWD 'StellaSora-integrity.json'),
    [switch] $Repair
)
$ErrorActionPreference = 'Stop'
if (-not ('StellaSoraMaintenance' -as [type])) {
    Add-Type -CompilerOptions '/unsafe' -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static unsafe class StellaSoraMaintenance
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Progress(int phase, long current, long total);
    public static int Run(string library, string root, string region, string report, bool repair)
    {
        bool cancelled = false;
        long last = 0;
        ConsoleCancelEventHandler cancel = (sender, e) => { cancelled = true; e.Cancel = true; };
        Progress progress = (phase, current, total) =>
        {
            if (Environment.TickCount64 - last > 1000)
            {
                last = Environment.TickCount64;
                Console.WriteLine("Phase {0}: {1:N0} / {2:N0} bytes", phase, current, total);
            }
            return cancelled ? 1 : 0;
        };
        Console.CancelKeyPress += cancel;
        try
        {
            nint module = NativeLibrary.Load(library);
            nint resolver = NativeLibrary.GetExport(module, "TryGetApiExport");
            string name = repair ? "RepairStellaSoraGame" : "CheckStellaSoraGameIntegrity";
            void* export = null;
            fixed (char* key = name)
            {
                int status = ((delegate* unmanaged[Cdecl]<char*, void**, int>)resolver)(key, &export);
                if (status != 0 || export == null) throw new InvalidOperationException("Missing maintenance API export.");
            }
            int result;
            fixed (char* path = root, id = region, output = report)
                result = ((delegate* unmanaged[Cdecl]<char*, int, char*, int, char*, int, nint, int>)export)(
                    path, root.Length, id, region.Length, output, report.Length, Marshal.GetFunctionPointerForDelegate(progress));
            GC.KeepAlive(progress);
            if (result < 0) Marshal.ThrowExceptionForHR(result);
            return result;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }
}
'@
}
$library = (Resolve-Path -LiteralPath $LibraryPath).Path
$game = (Resolve-Path -LiteralPath $GamePath).Path
$report = [IO.Path]::GetFullPath($ReportPath)
$result = [StellaSoraMaintenance]::Run($library, $game, $Region, $report, $Repair.IsPresent)
[pscustomobject]@{ Healthy = $result -eq 0; ReportPath = $report; RepairRequested = $Repair.IsPresent }
