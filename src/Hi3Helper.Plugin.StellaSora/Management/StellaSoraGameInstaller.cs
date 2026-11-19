using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices.Marshalling;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Utility;

namespace Hi3Helper.Plugin.StellaSora.Management;

[GeneratedComClass]
internal partial class StellaSoraGameInstaller(StellaSoraGameManager manager) : GameInstallerBase(manager)
{
    private readonly Action<string> _ensureIdle = root => GameProcesses.EnsureIdle(root, manager.Region);
    private readonly Func<string, long?> _fileLength = ReadFileLength;

    internal StellaSoraGameInstaller(StellaSoraGameManager manager, Action<string> ensureIdle,
        Func<string, long?>? fileLength = null) : this(manager)
    {
        _ensureIdle = ensureIdle;
        _fileLength = fileLength ?? ReadFileLength;
    }

    private static long? ReadFileLength(string path)
    {
        FileInfo info = new(path);
        return info.Exists ? info.Length : null;
    }

    protected override Task<int> InitAsync(CancellationToken token) => Task.FromResult(0);

    public override void Dispose()
    {
    }

    protected override Task StartInstallAsyncInner(InstallProgressDelegate? progress,
        InstallProgressStateDelegate? state, CancellationToken token) =>
        Task.Run(() => InstallAsync(progress, state, token), token);

    protected override Task StartUpdateAsyncInner(InstallProgressDelegate? progress,
        InstallProgressStateDelegate? state, CancellationToken token) =>
        Task.Run(() => InstallAsync(progress, state, token), token);

    protected override Task StartPreloadAsyncInner(InstallProgressDelegate? progress,
        InstallProgressStateDelegate? state, CancellationToken token) =>
        Task.FromException(
            new NotSupportedException("The official API does not currently expose a separate preload manifest."));

    internal static string CachePath(string root, GameFile file) => SafePaths.Resolve(root,
        StellaSoraConstants.WorkDirectory + "/cache/" + file.CacheName);

    protected override Task<long> GetGameSizeAsyncInner(GameInstallerKind kind, CancellationToken token) =>
        Task.Run(() => CalculateSizeAsync(kind, token), token);

    private async Task<long> CalculateSizeAsync(GameInstallerKind kind, CancellationToken token)
    {
        if (kind is not (GameInstallerKind.Install or GameInstallerKind.Update)) return 0;
        (_, GameManifest manifest) = await manager.GetManifestAsync(token).ConfigureAwait(false);
        if (kind == GameInstallerKind.Install) return manifest.TotalSize;
        manager.GetGamePath(out string? root);
        if (string.IsNullOrWhiteSpace(root)) return manifest.TotalSize;
        Dictionary<string, GameFile> old = InstalledMetadata.ReadFiles(root, manager.Region)
            .ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (GameFile file in manifest.Files)
        {
            token.ThrowIfCancellationRequested();
            if (!LikelyInstalled(root, file, old)) total = checked(total + file.Size);
        }

        return total;
    }

    protected override Task<long> GetGameDownloadedSizeAsyncInner(GameInstallerKind kind, CancellationToken token) =>
        Task.Run(() => CalculateDownloadedSizeAsync(kind, token), token);

    private async Task<long> CalculateDownloadedSizeAsync(GameInstallerKind kind, CancellationToken token)
    {
        if (kind is not (GameInstallerKind.Install or GameInstallerKind.Update)) return 0;
        (_, GameManifest manifest) = await manager.GetManifestAsync(token).ConfigureAwait(false);
        manager.GetGamePath(out string? root);
        if (string.IsNullOrWhiteSpace(root)) return 0;
        Dictionary<string, GameFile> old = InstalledMetadata.ReadFiles(root, manager.Region)
            .ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (GameFile file in manifest.Files)
        {
            token.ThrowIfCancellationRequested();
            total = checked(total + (LikelyInstalled(root, file, old)
                ? kind == GameInstallerKind.Install ? file.Size : 0
                : CachedSize(root, file)));
        }

        return total;
    }

    private bool LikelyInstalled(string root, GameFile file, Dictionary<string, GameFile> old) =>
        old.TryGetValue(file.Path, out GameFile? previous) && previous == file &&
        _fileLength(SafePaths.ResolveForRead(root, file.Path)) == file.Size;

    private long CachedSize(string root, GameFile file)
    {
        string path = SafePaths.ResolveForRead(root, StellaSoraConstants.WorkDirectory + "/cache/" + file.CacheName);
        return Math.Min(file.Size, _fileLength(path) ?? 0);
    }

    internal async Task InstallAsync(InstallProgressDelegate? progress, InstallProgressStateDelegate? state,
        CancellationToken token)
    {
        string root = Path.GetFullPath(EnsureAndGetGamePath());
        _ensureIdle(root);
        string work = SafePaths.Resolve(root, StellaSoraConstants.WorkDirectory);
        Directory.CreateDirectory(work);
        await using FileStream operationLock = new(
            SafePaths.Resolve(root, StellaSoraConstants.WorkDirectory + "/operation.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        InstalledMetadata.EnsureRegion(root, manager.Region);
        state?.Invoke(InstallProgressState.Preparing);
        // Refresh the release once per operation; an old UI snapshot must not select an obsolete package.
        await manager.InitializeAsync(token).ConfigureAwait(false);
        (GameRelease release, GameManifest manifest) = await manager.GetManifestAsync(token).ConfigureAwait(false);
        GameFile[] old = InstalledMetadata.ReadFiles(root, manager.Region);
        ConcurrentBag<GameFile> needed = [];
        state?.Invoke(InstallProgressState.Verify);
        ProgressReporter verificationProgress = new(progress, manifest.Files.Length, manifest.TotalSize);
        SafePaths.ReadScope readPaths = new(root);
        await Parallel.ForEachAsync(manifest.Files,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token },
            async (file, ct) =>
            {
                string destination = readPaths.Resolve(file.Path);
                if (!await Crc64.MatchesAsync(destination, file, ct, verificationProgress.Add).ConfigureAwait(false))
                    needed.Add(file);
                verificationProgress.FileDone();
            }).ConfigureAwait(false);
        GameFile[] downloads = needed.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        string cache = SafePaths.Resolve(root, StellaSoraConstants.WorkDirectory + "/cache");
        Directory.CreateDirectory(cache);
        long required = downloads.Sum(file => file.Size - CachedSize(root, file));
        DriveInfo drive = new(Path.GetPathRoot(root)!);
        if (drive.IsReady && drive.AvailableFreeSpace < required + 32L * 1024 * 1024)
            throw new IOException(
                "Insufficient free disk space. Existing game files are preserved until all downloads verify.");
        ProgressReporter downloadProgress = new(progress, downloads.Length, downloads.Sum(file => file.Size));
        state?.Invoke(InstallProgressState.Download);
        downloadProgress.Add(downloads.Sum(file => CachedSize(root, file)));
        nint throttle = SpeedLimiterService.CreateServiceContext();
        try
        {
            await Parallel.ForEachAsync(downloads,
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token },
                async (file, ct) =>
                {
                    await ImportOfficialPartialAsync(root, file, downloadProgress, ct).ConfigureAwait(false);
                    await DownloadAsync(root, manifest, file, downloadProgress, throttle, ct).ConfigureAwait(false);
                    downloadProgress.FileDone();
                }).ConfigureAwait(false);
        }
        finally
        {
            SpeedLimiterService.FreeServiceContext(throttle);
        }

        token.ThrowIfCancellationRequested();
        _ensureIdle(root);
        state?.Invoke(InstallProgressState.Install);
        // A pending marker blocks game launch if a move, metadata write, or cancellation interrupts the commit.
        InstalledMetadata.EnsureRegion(root, manager.Region);
        InstalledMetadata.WriteAtomic(root, InstalledMetadata.PendingName, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("tag", manager.Region.GameTag);
            writer.WriteString("version", release.VersionText);
            writer.WriteEndObject();
        });
        ProgressReporter installProgress = new(progress, downloads.Length, 0);
        foreach (GameFile file in downloads)
        {
            token.ThrowIfCancellationRequested();
            string destination = SafePaths.Resolve(root, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(CachePath(root, file), destination, true);
            installProgress.FileDone();
        }

        HashSet<string> currentPaths = manifest.Files.Select(x => x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (GameFile file in old)
        {
            token.ThrowIfCancellationRequested();
            if (!currentPaths.Contains(file.Path)) File.Delete(SafePaths.Resolve(root, file.Path));
        }

        InstalledMetadata.Commit(root, manager.Region, release, manifest);
        File.Delete(SafePaths.Resolve(root, InstalledMetadata.PendingName));
        manager.LoadConfig();
        if (InstalledMetadata.ReadVersion(root, manager.Region) != release.Version)
            throw new InvalidDataException("Installed version metadata verification failed.");
        state?.Invoke(InstallProgressState.Completed);
    }

    private static async Task ImportOfficialPartialAsync(string root, GameFile file, ProgressReporter progress,
        CancellationToken token)
    {
        string source = SafePaths.Resolve(root, file.Path + ".tmp");
        string target = CachePath(root, file);
        FileInfo partial = new(source);
        if (File.Exists(target) || !partial.Exists || partial.Length > file.Size) return;
        await using FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read, 131072,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using FileStream output = new(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072,
            FileOptions.Asynchronous);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(131072);
        try
        {
            int read;
            while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                progress.Add(read);
            }

            await output.FlushAsync(token).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task DownloadAsync(string root, GameManifest manifest, GameFile file, ProgressReporter progress,
        nint throttle, CancellationToken token)
    {
        string target = CachePath(root, file);
        if (await Crc64.MatchesAsync(target, file, token).ConfigureAwait(false)) return;
        Exception? last = null;
        for (int attempt = 0; attempt < manifest.Domains.Length * 3; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                long existing = File.Exists(target) ? new FileInfo(target).Length : 0;
                if (existing >= file.Size && File.Exists(target))
                {
                    File.Delete(target);
                    progress.Add(-Math.Min(existing, file.Size));
                }

                await DownloadOnceAsync(target,
                    manifest.GetFileUrl(manifest.Domains[attempt % manifest.Domains.Length], file), file.Size,
                    progress, throttle, token).ConfigureAwait(false);
                if (!await Crc64.MatchesAsync(target, file, token).ConfigureAwait(false))
                    throw new InvalidDataException("CRC64 verification failed: " + file.Path);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException ||
                                       ex is OperationCanceledException && !token.IsCancellationRequested)
            {
                last = ex;
                if (ex is InvalidDataException && File.Exists(target))
                {
                    progress.Add(-Math.Min(new FileInfo(target).Length, file.Size));
                    File.Delete(target);
                }

                if (attempt + 1 < manifest.Domains.Length * 3)
                    await Task.Delay(200 * (attempt + 1), token).ConfigureAwait(false);
            }
        }

        throw new IOException("Failed to download or verify the game file: " + file.Path, last);
    }

    private async Task DownloadOnceAsync(string target, Uri url, long expectedSize, ProgressReporter progress,
        nint throttle, CancellationToken token)
    {
        await using FileStream output = new(target, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 131072,
            FileOptions.Asynchronous);
        long offset = output.Length;
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        using HttpResponseMessage response = await manager.Client.Http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            output.SetLength(0);
            progress.Add(-offset);
            throw new IOException("The CDN rejected the resume range; retrying from the beginning.");
        }

        response.EnsureSuccessStatusCode();
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            ContentRangeHeaderValue? range = response.Content.Headers.ContentRange;
            if (range?.From != offset || range.Length != expectedSize || range.To != expectedSize - 1)
                throw new InvalidDataException("The CDN returned an unexpected content range.");
        }
        else if (response.StatusCode == HttpStatusCode.OK)
        {
            output.SetLength(0);
            progress.Add(-offset);
            offset = 0;
        }
        else throw new InvalidDataException("The CDN returned an unexpected download status.");

        if (response.Content.Headers.ContentLength is { } length && length != expectedSize - offset)
            throw new InvalidDataException("The CDN content length does not match the manifest.");
        output.Position = offset;
        await using Stream input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(131072);
        try
        {
            int read;
            while ((read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (output.Position + read > expectedSize)
                    throw new InvalidDataException("The download exceeds the manifest file size.");
                await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                progress.Add(read);
                timeout.CancelAfter(TimeSpan.FromSeconds(90));
                await SpeedLimiterService.AddBytesOrWaitAsync(throttle, read, token).ConfigureAwait(false);
            }

            await output.FlushAsync(token).ConfigureAwait(false);
            if (output.Length != expectedSize) throw new IOException("The CDN download is incomplete.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    protected override async Task UninstallAsyncInner(CancellationToken token)
    {
        string root = EnsureAndGetGamePath();
        _ensureIdle(root);
        string work = SafePaths.Resolve(root, StellaSoraConstants.WorkDirectory);
        Directory.CreateDirectory(work);
        await using FileStream operationLock = new(
            SafePaths.Resolve(root, StellaSoraConstants.WorkDirectory + "/operation.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        InstalledMetadata.EnsureRegion(root, manager.Region);
        GameFile[] files = InstalledMetadata.ReadFiles(root, manager.Region);
        if (files.Length == 0)
            throw new InvalidDataException(
                "Cannot safely uninstall without valid regional metadata. Repair the game first.");
        // Validate every target before the first deletion. Remove only manifest-owned files.
        string[] targets = files.Select(file => SafePaths.Resolve(root, file.Path)).ToArray();
        InstalledMetadata.WriteAtomic(root, InstalledMetadata.PendingName, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("tag", manager.Region.GameTag);
            writer.WriteBoolean("uninstall", true);
            writer.WriteEndObject();
        });
        foreach (string target in targets)
        {
            token.ThrowIfCancellationRequested();
            File.Delete(target);
        }

        File.Delete(SafePaths.Resolve(root, StellaSoraConstants.ConfigName));
        File.Delete(SafePaths.Resolve(root, StellaSoraConstants.ManifestName));
        File.Delete(SafePaths.Resolve(root, InstalledMetadata.PendingName));
        manager.LoadConfig();
    }

    private sealed class ProgressReporter(InstallProgressDelegate? callback, int count, long bytes)
    {
        private readonly Lock _sync = new();

        private InstallProgress _value = new()
        {
            TotalBytesToDownload = bytes, TotalCountToDownload = count, TotalStateToComplete = count
        };

        private long _last;
        private long _lastReportedBytes;

        internal void Add(long amount)
        {
            lock (_sync)
            {
                _value.DownloadedBytes += amount;
                Report(false);
            }
        }

        internal void FileDone()
        {
            lock (_sync)
            {
                _value.DownloadedCount++;
                _value.StateCount = _value.DownloadedCount;
                Report(_value.DownloadedCount == count);
            }
        }

        private void Report(bool force)
        {
            long now = Environment.TickCount64;
            if (!force && now - _last < 100 && !(_lastReportedBytes == 0 && _value.DownloadedBytes > 0)) return;
            _last = now;
            _lastReportedBytes = _value.DownloadedBytes;
            callback?.Invoke(in _value);
        }
    }
}
