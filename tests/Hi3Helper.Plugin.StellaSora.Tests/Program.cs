using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Management.Api;
using Hi3Helper.Plugin.Core.Utility;
using Hi3Helper.Plugin.StellaSora;
using Hi3Helper.Plugin.StellaSora.Api;
using Hi3Helper.Plugin.StellaSora.Configuration;
using Hi3Helper.Plugin.StellaSora.Management;

internal static class Program
{
    private static readonly StellaSoraRegion Region = StellaSoraRegion.Cn;
    private static int _passed;

    private static readonly string TestRoot = Path.GetFullPath(Path.Combine(".dev", "tests",
        DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]));

    private static async Task Main(string[] args)
    {
        Directory.CreateDirectory(TestRoot);
        int liveArg = Array.IndexOf(args, "--live");
        if (liveArg >= 0)
        {
            IReadOnlyList<StellaSoraRegion> regions = StellaSoraRegion.Supported;
            if (args.Length > liveArg + 1)
            {
                string id = args[liveArg + 1];
                regions = [regions.Single(region => region.Id.Equals(id, StringComparison.OrdinalIgnoreCase))];
            }

            foreach (StellaSoraRegion region in regions) await LiveAsync(region);
            return;
        }

        if (args.Contains("--benchmark"))
        {
            await BenchmarkAsync();
            return;
        }

        await Run("CRC64 independent test vectors", () =>
        {
            Equal(Crc64.Hash("123456789"u8), 0x995DC9BBDF1939FAUL);
            Equal(Crc64.Hash([]), 0UL);
        });
        await Run("Official launcher request signing", () =>
        {
            using JsonDocument auth = JsonDocument.Parse(LauncherClient.CreateAuthorization(1700000000, Region));
            JsonElement head = auth.RootElement.GetProperty("head");
            Equal(head.GetProperty("time").GetInt64(), 1700000000L);
            Equal(head.GetProperty("game_tag").GetString(), Region.GameTag);
            string expected =
                Convert.ToHexStringLower(
                    MD5.HashData(Encoding.UTF8.GetBytes(head.GetRawText() + Region.ApplicationSalt)));
            Equal(auth.RootElement.GetProperty("sign").GetString(), expected);
        });
        await RunAsync("Slicing CRC64 matches a bitwise reference and reports byte progress", async () =>
        {
            byte[] data = new byte[2 * 1024 * 1024];
            new Random(42).NextBytes(data);
            string file = Path.Combine(NewRoot("crc-progress"), "data.bin");
            await File.WriteAllBytesAsync(file, data);
            long processed = 0;
            int callbacks = 0;
            ulong actual = await Crc64.HashFileAsync(file, default, bytes =>
            {
                processed += bytes;
                callbacks++;
            });
            Equal(actual, ReferenceCrc64(data));
            Equal(processed, data.LongLength);
            Check(callbacks > 1, "A large hash did not report incremental progress");
        });
        await Run("Manifest path and collision validation", () =>
        {
            foreach (string path in new[]
                     {
                         "../escape", "/../escape", "C:/escape", "//server/share", "\\rooted", "dir/../../escape",
                         "a:stream", "CON.txt", "file.", "dir//file", ".collapse-stellasora/cache", "manifest.json"
                     })
                Throws<InvalidDataException>(() => SafePaths.NormalizeManifestPath(path));
            Equal(SafePaths.NormalizeManifestPath("/xtlr_Data/a.bin"), "xtlr_Data/a.bin");
            using JsonDocument json =
                JsonDocument.Parse(
                    "{\"source\":\"/pkg\",\"file\":[{\"path\":\"/xtlr.exe\",\"hash\":\"0\",\"size\":\"0\"},{\"path\":\"/XTLR.EXE\",\"hash\":\"0\",\"size\":\"0\"}]}");
            Throws<InvalidDataException>(() =>
                GameManifest.Parse(json.RootElement, [new("https://primary.test")], Region.Executable));
        });
        await Run("Plugin metadata and preset construction", () =>
        {
            StellaSoraPlugin plugin = new();
            plugin.GetPresetConfigCount(out int count);
            Equal(count, 5);
            plugin.GetPresetConfig(0, out var preset);
            preset.comGet_GameExecutableName(out string executable);
            Equal(executable, "xtlr.exe");
            preset.comGet_ZoneLogoUrl(out string icon);
            Check(Convert.FromBase64String(icon).Length > 1000, "Missing embedded icon");
            preset.Free();
        });
        await RunAsync("Installation size is available before choosing a directory", async () =>
        {
            using Fixture fixture = new(NewRoot("size-before-path"));
            fixture.Manager.SetGamePath("");
            Guid id = Guid.NewGuid();
            fixture.Installer.GetGameSizeAsync(GameInstallerKind.Install, in id, out nint size);
            Equal(await size.AsTask<long>(), fixture.Files.Sum(file => file.Size));
            id = Guid.NewGuid();
            fixture.Installer.GetGameDownloadedSizeAsync(GameInstallerKind.Install, in id, out nint downloaded);
            Equal(await downloaded.AsTask<long>(), 0L);
        });
        await RunAsync("Fresh install and official metadata round-trip", async () =>
        {
            using Fixture fixture = new(NewRoot("fresh"));
            await fixture.Install();
            Equal(InstalledMetadata.ReadVersion(fixture.Root, Region), fixture.Release.Version);
            Equal(InstalledMetadata.ReadFiles(fixture.Root, Region).Length, fixture.Files.Length);
            foreach (GameFile file in fixture.Files)
                Check(await Crc64.MatchesAsync(SafePaths.Resolve(fixture.Root, file.Path), file, default),
                    "Invalid installed file");
            fixture.Manager.IsGameInstalled(out bool installed);
            Check(installed, "Installation not detected");
            Check(fixture.Handler.Downloads.All(x => !x.Auth), "API authorization leaked to CDN");
        });
        await RunAsync("Cached size queries return before slow filesystem work", async () =>
        {
            using Fixture fixture = new(NewRoot("responsive-size"));
            await fixture.Install();
            using ManualResetEventSlim entered = new();
            using ManualResetEventSlim release = new();
            int callerThread = 0, diskThread = 0;
            StellaSoraGameInstaller installer = new(fixture.Manager, _ => { }, path =>
            {
                diskThread = Environment.CurrentManagedThreadId;
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Size query gate was not released");
                FileInfo info = new(path);
                return info.Exists ? info.Length : null;
            });
            TaskCompletionSource<nint> returned = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task caller = Task.Factory.StartNew(() =>
            {
                callerThread = Environment.CurrentManagedThreadId;
                Guid id = Guid.NewGuid();
                installer.GetGameDownloadedSizeAsync(GameInstallerKind.Install, in id, out nint result);
                returned.SetResult(result);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            nint resultPointer;
            try
            {
                resultPointer = await returned.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Check(entered.Wait(TimeSpan.FromSeconds(2)), "The filesystem query did not start");
                Check(callerThread != diskThread, "Disk scanning ran on the COM caller thread");
            }
            finally
            {
                release.Set();
            }

            Equal(await resultPointer.AsTask<long>(), fixture.Files.Sum(file => file.Size));
            await caller;
            installer.Dispose();
        });
        await RunAsync("Verification progress counts actual files and bytes", async () =>
        {
            using Fixture fixture = new(NewRoot("verify-progress"));
            InstallProgressState state = InstallProgressState.Idle;
            List<InstallProgress> snapshots = [];
            await fixture.Installer.InstallAsync((in InstallProgress value) =>
            {
                if (state == InstallProgressState.Verify) snapshots.Add(value);
            }, value => state = value, default);
            Check(snapshots.Count > 0, "Verification did not report progress");
            Check(snapshots.All(value => value.TotalStateToComplete == fixture.Files.Length),
                "Verification used phase counts instead of file counts");
            Equal(snapshots[^1].StateCount, fixture.Files.Length);
            Equal(snapshots[^1].DownloadedBytes, fixture.Files.Sum(file => file.Size));
        });
        await RunAsync("Official partial copy reports progress and supports cancellation", async () =>
        {
            byte[] content = new byte[8 * 1024 * 1024];
            using Fixture fixture = new(NewRoot("copy-cancel"), contents: new() { ["xtlr.exe"] = content });
            string source = SafePaths.Resolve(fixture.Root, "xtlr.exe.tmp");
            await File.WriteAllBytesAsync(source, content);
            using CancellationTokenSource cancel = new();
            InstallProgressState state = InstallProgressState.Idle;
            await ThrowsAsync<OperationCanceledException>(() => fixture.Installer.InstallAsync(
                (in InstallProgress value) =>
                {
                    if (state == InstallProgressState.Download && value.DownloadedBytes > 0) cancel.Cancel();
                }, value => state = value, cancel.Token));
            long cached = new FileInfo(StellaSoraGameInstaller.CachePath(fixture.Root, fixture.Files[0])).Length;
            Check(cached > 0 && cached < content.LongLength, "Copy did not leave a resumable partial cache");
            Equal(new FileInfo(source).Length, content.LongLength);
            Equal(InstalledMetadata.ReadVersion(fixture.Root, Region), GameVersion.Empty);
            await fixture.Install();
            Check(fixture.Handler.Downloads.Any(value => value.Offset == cached), "Cancelled copy did not resume");
        });
        await RunAsync("206 resume preserves existing bytes", async () =>
        {
            using Fixture fixture = new(NewRoot("resume"));
            fixture.SeedPartial(7);
            await fixture.Install();
            Check(fixture.Handler.Downloads.Any(x => x.Offset == 7), "Range request not sent");
        });
        await RunAsync("200 response safely restarts an ignored Range", async () =>
        {
            using Fixture fixture = new(NewRoot("ignored-range"));
            fixture.SeedPartial(9);
            fixture.Handler.IgnoreRange = true;
            await fixture.Install();
            Check(
                await Crc64.MatchesAsync(SafePaths.Resolve(fixture.Root, fixture.Files[0].Path), fixture.Files[0],
                    default), "Restart appended corrupt data");
        });
        await RunAsync("CRC64 failure falls back to backup CDN", async () =>
        {
            using Fixture fixture = new(NewRoot("backup"));
            fixture.Handler.CorruptPrimary = true;
            await fixture.Install();
            Check(fixture.Handler.Downloads.Any(x => x.Host == "backup.test"), "Backup CDN not used");
        });
        await RunAsync("416 resume rejection restarts safely", async () =>
        {
            using Fixture fixture = new(NewRoot("range-rejected"));
            fixture.SeedPartial(7);
            fixture.Handler.RejectRange = true;
            await fixture.Install();
            Check(
                fixture.Handler.Downloads.Any(x => x.Offset == 7) &&
                fixture.Handler.Downloads.Any(x => x.Host == "backup.test"), "Rejected range did not recover");
        });
        await RunAsync("Invalid Content-Range is rejected before appending", async () =>
        {
            using Fixture fixture = new(NewRoot("bad-range"));
            fixture.SeedPartial(7);
            fixture.Handler.WrongRange = true;
            await fixture.Install();
            Check(
                await Crc64.MatchesAsync(SafePaths.Resolve(fixture.Root, fixture.Files[0].Path), fixture.Files[0],
                    default), "Invalid range corrupted the file");
        });
        await Run("Process detection uses the exact executable path", () =>
        {
            StellaSoraRegion processRegion = Region with { Executable = Path.GetFileName(Environment.ProcessPath!) };
            using var self = GameProcesses.FindGame(AppContext.BaseDirectory, processRegion);
            Equal(self?.Id, Environment.ProcessId);
            using var unrelated = GameProcesses.FindGame(NewRoot("wrong-process-root"), processRegion);
            Check(unrelated == null, "An executable from another directory was incorrectly matched");
        });
        await RunAsync("Repair detects same-size corruption", async () =>
        {
            using Fixture fixture = new(NewRoot("repair"));
            await fixture.Install();
            byte[] corrupt = fixture.Contents[fixture.Files[0].Path].ToArray();
            corrupt[0] ^= 0xFF;
            await File.WriteAllBytesAsync(SafePaths.Resolve(fixture.Root, fixture.Files[0].Path), corrupt);
            int requests = fixture.Handler.Downloads.Count;
            await fixture.Install();
            Equal(fixture.Handler.Downloads.Count, requests + 1);
            Check(
                await Crc64.MatchesAsync(SafePaths.Resolve(fixture.Root, fixture.Files[0].Path), fixture.Files[0],
                    default), "Repair did not fix corruption");
        });
        await RunAsync("Download cancellation preserves installed version and cache", async () =>
        {
            using Fixture fixture = new(NewRoot("cancel"));
            await fixture.Install();
            GameRelease old = fixture.Release;
            fixture.AdvanceVersion();
            fixture.SeedPartial(5);
            using CancellationTokenSource cancel = new();
            fixture.Handler.OnDownload = ct =>
            {
                cancel.Cancel();
                ct.ThrowIfCancellationRequested();
            };
            await ThrowsAsync<OperationCanceledException>(() => fixture.Install(cancel.Token));
            Equal(InstalledMetadata.ReadVersion(fixture.Root, Region), old.Version);
            Check(File.Exists(StellaSoraGameInstaller.CachePath(fixture.Root, fixture.Files[0])),
                "Resume cache lost on cancellation");
        });
        await RunAsync("Interrupted commit blocks launch and recovers", async () =>
        {
            using Fixture fixture = new(NewRoot("commit-recovery"));
            using CancellationTokenSource cancel = new();
            await ThrowsAsync<OperationCanceledException>(() => fixture.Installer.InstallAsync(null,
                state =>
                {
                    if (state == InstallProgressState.Install) cancel.Cancel();
                }, cancel.Token));
            Equal(InstalledMetadata.ReadVersion(fixture.Root, Region), GameVersion.Empty);
            Check(File.Exists(SafePaths.Resolve(fixture.Root, StellaSoraConstants.WorkDirectory + "/pending.json")),
                "Pending marker missing");
            await fixture.Install();
            Equal(InstalledMetadata.ReadVersion(fixture.Root, Region), fixture.Release.Version);
        });
        await RunAsync("Updates remove only obsolete manifest-owned files", async () =>
        {
            using Fixture fixture = new(NewRoot("update"));
            await fixture.Install();
            string unrelated = SafePaths.Resolve(fixture.Root, "my-settings.txt");
            await File.WriteAllTextAsync(unrelated, "keep");
            string obsolete = fixture.Files[1].Path;
            fixture.AdvanceVersion(removeSecond: true);
            await fixture.Install();
            Check(!File.Exists(SafePaths.Resolve(fixture.Root, obsolete)), "Obsolete file retained");
            Equal(await File.ReadAllTextAsync(unrelated), "keep");
        });
        await RunAsync("Official partial files are copied and preserved", async () =>
        {
            using Fixture fixture = new(NewRoot("official-partial"));
            string temporary = SafePaths.Resolve(fixture.Root, fixture.Files[0].Path + ".tmp");
            byte[] partial = fixture.Contents[fixture.Files[0].Path][..6];
            await File.WriteAllBytesAsync(temporary, partial);
            await fixture.Install();
            Check((await File.ReadAllBytesAsync(temporary)).SequenceEqual(partial),
                "Official partial file was modified");
            Check(fixture.Handler.Downloads.Any(x => x.Offset == 6), "Official partial file was not reused");
        });
        await RunAsync("Regional metadata rejects another region", async () =>
        {
            using Fixture fixture = new(NewRoot("region"));
            await fixture.Install();
            StellaSoraRegion other = StellaSoraRegion.Tw;
            Equal(InstalledMetadata.ReadVersion(fixture.Root, other), GameVersion.Empty);
            Equal(InstalledMetadata.ReadFiles(fixture.Root, other).Length, 0);
            using Fixture otherFixture = new(fixture.Root, region: other);
            await ThrowsAsync<InvalidOperationException>(() => otherFixture.Install());
            Equal(InstalledMetadata.ReadVersion(fixture.Root, Region), fixture.Release.Version);
            using JsonDocument header = JsonDocument.Parse(LauncherClient.CreateAuthorization(1700000000, other));
            Equal(header.RootElement.GetProperty("head").GetProperty("game_tag").GetString(), other.GameTag);
        });
        await RunAsync("Tampered official config is not accepted", async () =>
        {
            using Fixture fixture = new(NewRoot("tampered"));
            await fixture.Install();
            string config = SafePaths.Resolve(fixture.Root, StellaSoraConstants.ConfigName);
            string text = await File.ReadAllTextAsync(config);
            await File.WriteAllTextAsync(config, text.Replace("2.0.0", "9.0.0"));
            Equal(InstalledMetadata.ReadVersion(fixture.Root, Region), GameVersion.Empty);
            Equal(await File.ReadAllTextAsync(config), text.Replace("2.0.0", "9.0.0"));
        });
        await RunAsync("Traditional Chinese metadata matches the official extended schema", async () =>
        {
            StellaSoraRegion region = StellaSoraRegion.Tw;
            using Fixture fixture = new(NewRoot("tw-metadata", region), region: region);
            await fixture.Install();
            string config = SafePaths.Resolve(fixture.Root, StellaSoraConstants.ConfigName);
            using JsonDocument json = JsonDocument.Parse(await File.ReadAllBytesAsync(config));
            JsonElement metadata = json.RootElement;
            Equal(metadata.GetProperty("params").GetArrayLength(), 0);
            Equal(metadata.GetProperty("gameUninstallScript").GetString(), "");
            string signedValues = "StellaSora_TW;StellaSora;;2.0.0;";
            Equal(metadata.GetProperty("vc").GetString(),
                Convert.ToBase64String(MD5.HashData(Encoding.UTF8.GetBytes(signedValues))));
            Equal(InstalledMetadata.ReadVersion(fixture.Root, region), fixture.Release.Version);
        });
        await RunAsync("Uninstall preserves unrelated files", async () =>
        {
            using Fixture fixture = new(NewRoot("uninstall"));
            await fixture.Install();
            string user = SafePaths.Resolve(fixture.Root, "user-data.txt");
            await File.WriteAllTextAsync(user, "preserve");
            Guid id = Guid.NewGuid();
            fixture.Installer.UninstallAsync(in id, out nint result);
            await result.AsTask();
            Equal(await File.ReadAllTextAsync(user), "preserve");
            Equal(InstalledMetadata.ReadVersion(fixture.Root, Region), GameVersion.Empty);
        });
        await RunAsync("News, banners and social resources map to Core ABI", async () =>
        {
            using Fixture fixture = new(NewRoot("news"));
            using StellaSoraLauncherNews news = new(Region, new HttpClient(fixture.Handler, false));
            await news.InitializeAsync(default);
            Equal(news.News.Length, 2);
            Equal(news.Banners.Length, 1);
            Equal(news.Socials.Length, 6);
            Equal(news.News[0].Type, LauncherNewsEntryType.Notice);
            ValidateNewsMemory(news);
            ValidateSocialMemory(news, ["官网", "微信公众号", "bilibili", "微博", "抖音", "联系客服"]);
        });
        await RunAsync("Traditional Chinese social entries use distinct white launcher icons", async () =>
        {
            StellaSoraRegion region = StellaSoraRegion.Tw;
            using Fixture fixture = new(NewRoot("tw-social", region), region: region);
            string[] channels = ["Facebook", "YouTube", "官网", "Discord"];
            fixture.Handler.SocialChannels = channels;
            using StellaSoraLauncherNews news = new(region, new HttpClient(fixture.Handler, false));
            await news.InitializeAsync(default);
            ValidateSocialMemory(news, channels, whiteIcons: true);
        });
        await Run("Launcher notice lists classify and deduplicate localized entries", () =>
        {
            using StellaSoraLauncherNews news = new(StellaSoraRegion.Kr);
            using JsonDocument fixture = JsonDocument.Parse("""
                {
                  "operations_resource_open": true,
                  "news_list": null,
                  "notice_list": [
                    {"notice_type":"전체","notice_detail_list":[{"notice_title":"Notice","notice_time":"2026/11/19","jump_url":"https://example.test/1"}]},
                    {"notice_type":"공지 사항","notice_detail_list":[{"notice_title":"Notice","notice_time":"2026/11/19","jump_url":"https://example.test/1"}]},
                    {"notice_type":"이벤트 공지","notice_detail_list":[{"notice_title":"Event","notice_time":"2026/11/18","jump_url":"https://example.test/2"}]}
                  ],
                  "operations_banner_list": []
                }
                """);
            news.ParseOperations(fixture.RootElement, DateTimeOffset.UtcNow);
            Equal(news.News.Length, 2);
            Equal(news.News[0].Type, LauncherNewsEntryType.Notice);
            Equal(news.News[0].Date, "11/19");
            Equal(news.News[1].Type, LauncherNewsEntryType.Event);
        });
        Console.WriteLine($"PASS: {_passed} checks. Isolated fixtures: {TestRoot}");
    }

    private static ulong ReferenceCrc64(ReadOnlySpan<byte> bytes)
    {
        ulong crc = ulong.MaxValue;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xC96C5795D7870F42UL);
        }

        return ~crc;
    }

    private static unsafe void ValidateSocialMemory(StellaSoraLauncherNews news, string[] channels, bool whiteIcons = false)
    {
        news.GetSocialMediaEntries(out nint pointer, out int count, out bool disposable, out bool allocated);
        Check(disposable && allocated && count == channels.Length, "Invalid social ABI allocation");
        var memory = new PluginDisposableMemory<LauncherSocialMediaEntry>((LauncherSocialMediaEntry*)pointer, count);
        HashSet<string> images = [];
        try
        {
            for (int i = 0; i < count; i++)
            {
                Check(memory[i].Flags.HasFlag(LauncherSocialMediaEntryFlag.IconIsDataBuffer),
                    "Social icon has incorrect flags");
                Check(memory[i].Flags.HasFlag(LauncherSocialMediaEntryFlag.IconHoverIsDataBuffer),
                    "Social hover icon has incorrect flags");
                byte[] bytes = System.Buffers.Text.Base64Url.DecodeFromChars(memory[i].IconPath);
                Check(bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
                    "Social icon is not a PNG");
                images.Add(Convert.ToHexString(SHA256.HashData(bytes)));
                byte[] hover = System.Buffers.Text.Base64Url.DecodeFromChars(memory[i].IconHoverPath);
                Equal(bytes.SequenceEqual(hover), whiteIcons);
                Equal(news.Socials[i].Name, channels[i]);
                Equal(memory[i].ClickUrl, news.Socials[i].Url);
                if (channels[i] == "微信公众号")
                    Check(memory[i].Flags.HasFlag(LauncherSocialMediaEntryFlag.QrImageIsPath), "WeChat QR resource was lost");
            }

            Equal(images.Count, channels.Length);
        }
        finally
        {
            for (int i = 0; i < count; i++) memory[i].Dispose();
            memory.Dispose();
        }
    }

    private static unsafe void ValidateNewsMemory(StellaSoraLauncherNews news)
    {
        news.GetNewsEntries(out nint pointer, out int count, out bool disposable, out bool allocated);
        Check(disposable && allocated && count == 2, "Invalid ABI memory flags");
        var memory = new PluginDisposableMemory<LauncherNewsEntry>((LauncherNewsEntry*)pointer, count);
        try
        {
            Equal(memory[0].Title, "Notice");
        }
        finally
        {
            for (int i = 0; i < count; i++) memory[i].Dispose();
            memory.Dispose();
        }
    }

    private static async Task BenchmarkAsync()
    {
        using JsonDocument json = JsonDocument.Parse(await File.ReadAllBytesAsync(".dev/research/manifest.json"));
        GameManifest manifest = GameManifest.Parse(json.RootElement,
            [new Uri("https://primary.test"), new Uri("https://backup.test")], Region.Executable);
        using Fixture fixture = new(NewRoot("full-manifest-benchmark"), manifest.Files,
            new Dictionary<string, byte[]>());
        fixture.Handler.LiveManifest = manifest;
        await fixture.Manager.GetManifestAsync(default);
        Guid id = Guid.NewGuid();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        fixture.Installer.GetGameDownloadedSizeAsync(GameInstallerKind.Install, in id, out nint result);
        double callerMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        long downloaded = await result.AsTask<long>();
        double sizeMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        using CancellationTokenSource cancel = new();
        InstallProgressState state = InstallProgressState.Idle;
        int verified = 0;
        stopwatch.Restart();
        await ThrowsAsync<OperationCanceledException>(() => fixture.Installer.InstallAsync((in InstallProgress value) =>
        {
            if (state == InstallProgressState.Verify) verified = value.StateCount;
        }, value =>
        {
            state = value;
            if (value == InstallProgressState.Download) cancel.Cancel();
        }, cancel.Token));
        double verificationMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        Equal(verified, manifest.Files.Length);
        Equal(downloaded, 0L);
        var report = new
        {
            Files = manifest.Files.Length, CallerMilliseconds = callerMilliseconds,
            SizeScanMilliseconds = sizeMilliseconds, VerificationMilliseconds = verificationMilliseconds,
            VerifiedFiles = verified
        };
        await File.WriteAllTextAsync(".dev/benchmark-last.json",
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(report));
        Console.WriteLine("PASS: full-manifest size and verification scans; cancelled before download. Fixture: " +
                          fixture.Root);
    }

    private static async Task LiveAsync(StellaSoraRegion region)
    {
        using HttpClient http = LauncherClient.CreateHttpClient();
        LauncherClient client = new(http, region);
        GameRelease release = await client.GetReleaseAsync(default);
        GameManifest manifest = await client.GetManifestAsync(release, default);
        Check(manifest.Files.Length > 1000, "Incomplete live manifest");
        Console.WriteLine(
            $"{region.Id}: {release.VersionText}, {manifest.Files.Length} files, {manifest.TotalSize} bytes.");
        using StellaSoraLauncherNews news = new(region);
        await news.InitializeAsync(default);
        Console.WriteLine(
            $"Official content: {news.News.Length} news, {news.Banners.Length} banners, {news.Socials.Length} social entries.");
        GameFile[] sample = manifest.Files.Where(x =>
            x.Path.Equals(region.Executable, StringComparison.OrdinalIgnoreCase) ||
            x.Path.EndsWith("/app.info", StringComparison.OrdinalIgnoreCase)).ToArray();
        Equal(sample.Length, 2);
        string appInfo = sample.Single(file => file.Path.EndsWith("/app.info", StringComparison.OrdinalIgnoreCase)).Path;
        using Fixture fixture = new(NewRoot("live-" + region.Id, region), sample, new Dictionary<string, byte[]>(), region);
        fixture.Handler.LiveManifest = manifest with { Files = sample };
        fixture.Handler.LiveRelease = release;
        fixture.Handler.ForwardHttp = http;
        await fixture.Install();
        foreach (GameFile file in sample)
            Check(await Crc64.MatchesAsync(SafePaths.Resolve(fixture.Root, file.Path), file, default),
                "Live CRC64 mismatch");
        string info = await File.ReadAllTextAsync(SafePaths.Resolve(fixture.Root, appInfo));
        string[] appInfoLines = info.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Equal(appInfoLines[0], region.Vendor);
        Equal(appInfoLines[1], region.AppDataDirectory);
        Console.WriteLine("Official Unity app.info: " + info.Replace('\n', '/').Replace('\r', ' '));
        Console.WriteLine(
            $"PASS: live signed API, news/media, two-file CDN installation, CRC64 and metadata. Sample: {fixture.Root}");
    }

    private static string NewRoot(string name, StellaSoraRegion? region = null)
    {
        string root = Path.Combine(TestRoot, name, (region ?? Region).DirectoryName);
        Directory.CreateDirectory(root);
        return root;
    }

    private static Task Run(string name, Action test)
    {
        test();
        _passed++;
        Console.WriteLine("PASS " + name);
        return Task.CompletedTask;
    }

    private static async Task RunAsync(string name, Func<Task> test)
    {
        await test();
        _passed++;
        Console.WriteLine("PASS " + name);
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T actual, T expected)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
            throw new InvalidOperationException($"Expected {expected}, got {actual}");
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; }
        internal FixtureHandler Handler { get; }
        internal StellaSoraGameManager Manager { get; }
        internal StellaSoraGameInstaller Installer { get; }

        internal GameRelease Release => Handler.LiveRelease ??
                                        new(new GameVersion(Handler.Version), Handler.Version, "prod/game.zip");

        internal GameFile[] Files => Handler.Files;
        internal Dictionary<string, byte[]> Contents => Handler.Contents;

        internal Fixture(string root, GameFile[]? files = null, Dictionary<string, byte[]>? contents = null,
            StellaSoraRegion? region = null)
        {
            region ??= Region;
            Root = root;
            contents ??= new()
            {
                [region.Executable] = Encoding.UTF8.GetBytes("fixture-executable-never-run"),
                [region.ExecutableStem + "_Data/test.bin"] = Encoding.UTF8.GetBytes("fixture-game-resource")
            };
            files ??= contents.Select(x => new GameFile(x.Key, Crc64.Hash(x.Value), x.Value.LongLength)).ToArray();
            Handler = new(files, contents, region);
            Manager = new(region, new HttpClient(Handler, false));
            Manager.SetGamePath(root);
            Installer = new(Manager,
                path => Check(
                    path.StartsWith(TestRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                    "Test escaped fixture directory"));
        }

        internal Task Install(CancellationToken token = default) => Installer.InstallAsync(null, null, token);

        internal void SeedPartial(int bytes)
        {
            Directory.CreateDirectory(SafePaths.Resolve(Root, StellaSoraConstants.WorkDirectory + "/cache"));
            File.WriteAllBytes(StellaSoraGameInstaller.CachePath(Root, Files[0]), Contents[Files[0].Path][..bytes]);
        }

        internal void AdvanceVersion(bool removeSecond = false)
        {
            Handler.Version = "2.1.0";
            Contents[Files[0].Path] = Encoding.UTF8.GetBytes("updated-fixture-executable-never-run");
            if (removeSecond) Contents.Remove(Files[1].Path);
            Handler.Files = Contents.Select(x => new GameFile(x.Key, Crc64.Hash(x.Value), x.Value.LongLength))
                .ToArray();
        }

        public void Dispose()
        {
            Installer.Dispose();
            Manager.Dispose();
            Handler.Dispose();
        }
    }

    private sealed class FixtureHandler(GameFile[] files, Dictionary<string, byte[]> contents, StellaSoraRegion region)
        : HttpMessageHandler
    {
        internal GameFile[] Files = files;
        internal Dictionary<string, byte[]> Contents = contents;
        internal string Version = "2.0.0";
        internal bool IgnoreRange;
        internal bool CorruptPrimary;
        internal bool RejectRange;
        internal bool WrongRange;
        internal string[]? SocialChannels;
        internal Action<CancellationToken>? OnDownload;
        internal GameManifest? LiveManifest;
        internal GameRelease? LiveRelease;
        internal HttpClient? ForwardHttp;
        internal ConcurrentBag<(string Host, long Offset, bool Auth)> Downloads = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken token)
        {
            Uri uri = request.RequestUri!;
            if (uri.Host == new Uri(region.ApiBase).Host && uri.AbsolutePath.StartsWith("/api/launcher/", StringComparison.Ordinal))
            {
                Check(request.Headers.Contains("Authorization"), "Missing API authorization");
                object data = uri.AbsolutePath switch
                {
                    "/api/launcher/game/config" => new
                    {
                        game_latest_version = LiveRelease?.VersionText ?? Version,
                        game_latest_file_path = LiveRelease?.Basis ?? "prod/game.zip",
                        game_start_exe_name = region.ExecutableStem
                    },
                    "/api/launcher/game/config/json" => new { url = "https://primary.test/manifest.json" },
                    "/api/launcher/advanced/game/download/cdn" => new
                    {
                        primary_cdn = LiveManifest?.Domains[0].AbsoluteUri ?? "https://primary.test",
                        back_up_cdn = LiveManifest?.Domains.Last().AbsoluteUri ?? "https://backup.test"
                    },
                    "/api/launcher/base/config" => new { launcher_background_img = region.Background },
                    "/api/launcher/operations/resource" => Operations(),
                    "/api/launcher/social/media/resource" => new
                    {
                        social_media_resource_open = true,
                        social_media_resource_list = SocialChannels?.Select(channel => new
                        {
                            social_media_channel = channel, qr_img = "", jump_url = "https://example.test/" + Uri.EscapeDataString(channel)
                        }).ToArray() ?? new[]
                        {
                            new { social_media_channel = "官网", qr_img = "", jump_url = "https://example.test" },
                            new
                            {
                                social_media_channel = "微信公众号", qr_img = "https://example.test/qr.png", jump_url = ""
                            },
                            new
                            {
                                social_media_channel = "bilibili", qr_img = "",
                                jump_url = "https://example.test/bilibili"
                            },
                            new { social_media_channel = "微博", qr_img = "", jump_url = "https://example.test/weibo" },
                            new { social_media_channel = "抖音", qr_img = "", jump_url = "https://example.test/douyin" },
                            new
                            {
                                social_media_channel = "联系客服", qr_img = "", jump_url = "https://example.test/support"
                            }
                        }
                    },
                    _ => throw new InvalidOperationException("Unknown fixture API: " + uri.AbsolutePath)
                };
                return Json(new { code = 200, data });
            }

            if (uri.Host == "primary.test" && uri.AbsolutePath == "/manifest.json")
                return Json(new
                {
                    source = "/" + (LiveManifest?.Source ?? "pkg"),
                    file = Files.Select(x => new { path = x.OfficialPath, hash = x.HashText, size = x.SizeText })
                });
            bool auth = request.Headers.Contains("Authorization");
            Check(!auth, "Authorization leaked to CDN");
            long offset = request.Headers.Range?.Ranges.First().From ?? 0;
            Downloads.Add((uri.Host, offset, auth));
            OnDownload?.Invoke(token);
            if (offset > 0 && RejectRange) return new(HttpStatusCode.RequestedRangeNotSatisfiable);
            if (ForwardHttp != null)
            {
                using HttpRequestMessage forward = new(HttpMethod.Get, uri);
                if (request.Headers.Range != null) forward.Headers.Range = request.Headers.Range;
                return await ForwardHttp.SendAsync(forward, HttpCompletionOption.ResponseHeadersRead, token);
            }

            string name = Uri.UnescapeDataString(uri.AbsolutePath["/pkg/".Length..]);
            byte[] bytes = Contents[name].ToArray();
            if (CorruptPrimary && uri.Host == "primary.test" && bytes.Length > 0) bytes[0] ^= 0x80;
            long start = IgnoreRange ? 0 : offset;
            HttpResponseMessage response = new(start > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
                { Content = new ByteArrayContent(bytes[(int)start..]) };
            if (start > 0)
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(WrongRange ? start + 1 : start,
                    bytes.Length - 1, bytes.Length);
            return response;
        }

        private static object Operations()
        {
            object Row(string title, string link) =>
                new { title, link, description = title, publishTime = 1700000000000L };

            return new
            {
                operations_resource_open = true,
                news_list = new
                {
                    data = new
                    {
                        news = new[]
                        {
                            new { typeLabel = "最新", rows = new[] { Row("Notice", "https://example.test/notice") } },
                            new { typeLabel = "公告", rows = new[] { Row("Notice", "https://example.test/notice") } },
                            new { typeLabel = "新闻", rows = new[] { Row("News", "https://example.test/news") } }
                        }
                    }
                },
                operations_banner_list = new[]
                {
                    new
                    {
                        banner_img = "https://example.test/current.png", jump_url = "https://example.test/news",
                        remark = "Current", start_at = 1L, end_at = 0L
                    },
                    new
                    {
                        banner_img = "https://example.test/expired.png", jump_url = "https://example.test/news",
                        remark = "Expired", start_at = 1L, end_at = 2L
                    }
                }
            };
        }

        private static HttpResponseMessage Json(object data) => new(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json") };
    }
}
