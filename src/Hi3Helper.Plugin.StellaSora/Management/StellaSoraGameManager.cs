using System.Net.Http;
using System.Runtime.InteropServices.Marshalling;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.StellaSora.Api;
using Hi3Helper.Plugin.StellaSora.Configuration;

namespace Hi3Helper.Plugin.StellaSora.Management;

[GeneratedComClass]
internal partial class StellaSoraGameManager : GameManagerBase
{
    private readonly SemaphoreSlim _manifestLock = new(1, 1);
    private GameManifest? _manifest;
    private GameRelease? _release;
    internal LauncherClient Client { get; }
    internal StellaSoraRegion Region { get; }

    internal StellaSoraGameManager(StellaSoraRegion? region = null, HttpClient? http = null)
    {
        Region = region ?? StellaSoraRegion.Cn;
        ApiResponseHttpClient = http ?? LauncherClient.CreateHttpClient();
        Client = new(ApiResponseHttpClient, Region);
    }

    protected override HttpClient ApiResponseHttpClient { get; set; }
    protected override bool HasPreload => false;

    protected override bool IsInstalled => !string.IsNullOrWhiteSpace(CurrentGameInstallPath) &&
                                           CurrentGameVersion != GameVersion.Empty &&
                                           InstalledMetadata.ReadVersion(CurrentGameInstallPath, Region) ==
                                           CurrentGameVersion;

    protected override bool HasUpdate =>
        IsInstalled && ApiGameVersion != GameVersion.Empty && CurrentGameVersion < ApiGameVersion;

    protected override void SetGamePathInner(string gamePath)
    {
        CurrentGameInstallPath = string.IsNullOrWhiteSpace(gamePath) ? null : Path.GetFullPath(gamePath);
        LoadConfig();
    }

    protected override void SetCurrentGameVersionInner(in GameVersion gameVersion)
    {
    }

    public override void LoadConfig() => CurrentGameVersion = string.IsNullOrWhiteSpace(CurrentGameInstallPath)
        ? GameVersion.Empty
        : InstalledMetadata.ReadVersion(CurrentGameInstallPath, Region);

    // The installer commits official metadata only after every target file verifies.
    // Host-side SaveConfig must never fabricate a completed installation.
    public override void SaveConfig() => LoadConfig();

    protected override Task<int> InitAsync(CancellationToken token) => InitializeAsync(token);

    internal async Task<int> InitializeAsync(CancellationToken token)
    {
        await _manifestLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            GameRelease release = await Client.GetReleaseAsync(token).ConfigureAwait(false);
            if (_release != release) _manifest = null;
            _release = release;
            ApiGameVersion = release.Version;
            LoadConfig();
            return 0;
        }
        finally
        {
            _manifestLock.Release();
        }
    }

    internal async Task<(GameRelease Release, GameManifest Manifest)> GetManifestAsync(CancellationToken token)
    {
        await _manifestLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            _release ??= await Client.GetReleaseAsync(token).ConfigureAwait(false);
            ApiGameVersion = _release.Version;
            _manifest ??= await Client.GetManifestAsync(_release, token).ConfigureAwait(false);
            return (_release, _manifest);
        }
        finally
        {
            _manifestLock.Release();
        }
    }

    protected override Task<string?> FindExistingInstallPathAsyncInner(CancellationToken token) =>
        Task.FromResult(FindInstallation(token));

    internal string? FindInstallation(CancellationToken token)
    {
        List<string> candidates = [];
        if (!string.IsNullOrWhiteSpace(CurrentGameInstallPath)) candidates.Add(CurrentGameInstallPath);
        foreach (DriveInfo drive in DriveInfo.GetDrives())
            if (drive.DriveType == DriveType.Fixed && drive.IsReady)
                candidates.Add(Path.Combine(drive.RootDirectory.FullName, Region.InstallParentDirectory, Region.DirectoryName));
        foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            if (InstalledMetadata.ReadVersion(candidate, Region) != GameVersion.Empty) return candidate;
        }

        return null;
    }
}
