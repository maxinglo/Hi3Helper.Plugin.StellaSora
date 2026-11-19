[CmdletBinding()]
param([Parameter(Mandatory)][string] $Path, [string] $ExpectedVersion = '0.2.1.0')
$ErrorActionPreference = 'Stop'
if (-not ('StellaSoraNativeSmoke' -as [type])) {
    Add-Type -CompilerOptions '/unsafe' -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static unsafe class StellaSoraNativeSmoke
{
    public static string Run(string path, string expectedVersion)
    {
        // NativeAOT libraries remain loaded until this process exits.
        nint module = NativeLibrary.Load(path);
        nint resolver = NativeLibrary.GetExport(module, "TryGetApiExport");
        nint Find(string name)
        {
            void* result = null;
            fixed (char* pointer = name)
            {
                int status = ((delegate* unmanaged[Cdecl]<char*, void**, int>)resolver)(pointer, &result);
                if (status != 0 || result == null) throw new InvalidOperationException("Missing API export: " + name);
            }
            return (nint)result;
        }
        Version ReadVersion(string name)
        {
            int* values = (int*)((delegate* unmanaged[Cdecl]<void*>)Find(name))();
            return new Version(values[0], values[1], values[2], values[3]);
        }
        Version standard = ReadVersion("GetPluginStandardVersion");
        Version version = ReadVersion("GetPluginVersion");
        if (standard != new Version(0, 1, 6, 0) || version != Version.Parse(expectedVersion))
            throw new InvalidOperationException("Unexpected API or plugin version.");
        foreach (string name in new[] { "LaunchGameFromGameManagerAsync", "IsGameRunning", "WaitRunningGameAsync", "KillRunningGame", "CheckStellaSoraGameIntegrity", "RepairStellaSoraGame" }) Find(name);
        nint unknown = (nint)((delegate* unmanaged[Cdecl]<void*>)Find("GetPlugin"))();
        nint plugin = 0;
        nint preset = 0;
        nint updater = 0;
        try
        {
            Guid id = new Guid("436f6c6c-6170-7365-727f-000000000010");
            void** unknownTable = *(void***)unknown;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[MemberFunction]<nint, Guid*, nint*, int>)unknownTable[0])(unknown, &id, &plugin));
            void** table = *(void***)plugin;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[MemberFunction]<nint, nint*, int>)table[15])(plugin, &updater));
            if (updater == 0) throw new InvalidOperationException("Missing plugin updater COM pointer.");
            void** updaterTable = *(void***)updater;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[MemberFunction]<nint, int>)updaterTable[3])(updater));

            string[] expectedSources =
            {
                "https://github.com/maxinglo/Hi3Helper.Plugin.StellaSora/releases/latest/download/",
                "https://gitee.com/maxing_long/hi3helper-plugin-stella-sora/releases/download/latest/"
            };
            int sourceCount = 0;
            char** sourceUrls = null;
            ((delegate* unmanaged[Cdecl]<int*, char***, void>)Find("GetPluginUpdateCdnList"))(&sourceCount, &sourceUrls);
            try
            {
                if (sourceUrls == null || sourceCount != expectedSources.Length)
                    throw new InvalidOperationException("Unexpected number of plugin update sources.");
                for (int index = 0; index < sourceCount; index++)
                    if (Marshal.PtrToStringUni((nint)sourceUrls[index]) != expectedSources[index])
                        throw new InvalidOperationException("Unexpected plugin update URL.");
            }
            finally
            {
                if (sourceUrls != null)
                {
                    for (int index = 0; index < sourceCount; index++) Marshal.FreeCoTaskMem((nint)sourceUrls[index]);
                    NativeMemory.Free(sourceUrls);
                }
            }

            int count = 0;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[MemberFunction]<nint, int*, int>)table[8])(plugin, &count));
            var profiles = new HashSet<string> { "StellaSoraCN", "StellaSoraEN", "StellaSoraJP", "StellaSoraKR", "StellaSoraTW" };
            if (count != profiles.Count) throw new InvalidOperationException("Unexpected region count.");
            string ReadString(nint instance, int slot)
            {
                void** vtable = *(void***)instance;
                nint text = 0;
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[MemberFunction]<nint, nint*, int>)vtable[slot])(instance, &text));
                try { return Marshal.PtrToStringUni(text) ?? ""; } finally { Marshal.FreeCoTaskMem(text); }
            }
            foreach (string locale in new[] { "en-US", "zh-CN" })
            {
                fixed (char* text = locale) Marshal.ThrowExceptionForHR(((delegate* unmanaged[MemberFunction]<nint, char*, int>)table[14])(plugin, text));
                string name = ReadString(plugin, 4);
                if (name != (locale == "en-US" ? "Stella Sora" : "星塔旅人")) throw new InvalidOperationException("Incorrect localized game name.");
            }
            string icon = ReadString(plugin, 9);
            if (Convert.FromBase64String(icon).Length < 1000) throw new InvalidOperationException("Embedded game icon is missing.");
            for (int index = 0; index < count; index++)
            {
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[MemberFunction]<nint, int, nint*, int>)table[11])(plugin, index, &preset));
                if (preset == 0) throw new InvalidOperationException("Missing preset COM pointer.");
                if (ReadString(preset, 5) != "星塔旅人") throw new InvalidOperationException("Incorrect preset game name.");
                if (!profiles.Remove(ReadString(preset, 6))) throw new InvalidOperationException("Unexpected or duplicate profile.");
                Marshal.Release(preset);
                preset = 0;
            }
        }
        finally
        {
            if (updater != 0) Marshal.Release(updater);
            if (preset != 0) Marshal.Release(preset);
            if (plugin != 0) Marshal.Release(plugin);
            if (unknown != 0) Marshal.Release(unknown);
        }
        return "PASS: NativeAOT exports, five COM presets, localized metadata, icon, and both update sources. Core " + standard + ", StellaSora " + version;
    }
}
'@
}
[StellaSoraNativeSmoke]::Run((Resolve-Path -LiteralPath $Path).Path, $ExpectedVersion)
