using System.Runtime.InteropServices.Marshalling;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management.Api;
using Hi3Helper.Plugin.StellaSora.Configuration;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.Plugin.StellaSora.Api;

[GeneratedComClass]
internal sealed partial class StellaSoraLauncherMedia : LauncherApiMediaBase, IDisposable
{
    private readonly LauncherClient _client;
    private string _background;

    internal StellaSoraLauncherMedia(StellaSoraRegion region, HttpClient? http = null)
    {
        ApiResponseHttpClient = http ?? LauncherClient.CreateHttpClient();
        _client = new(ApiResponseHttpClient, region);
        _background = region.Background;
    }

    protected override HttpClient ApiResponseHttpClient { get; set; }

    public override void GetBackgroundFlag(out LauncherBackgroundFlag result) =>
        result = LauncherBackgroundFlag.TypeIsImage | LauncherBackgroundFlag.IsSourceFile;

    public override void GetLogoFlag(out LauncherBackgroundFlag result) => result = LauncherBackgroundFlag.None;

    public override void GetBackgroundEntries(out nint handle, out int count, out bool disposable, out bool allocated)
    {
        PluginDisposableMemory<LauncherPathEntry> entries = PluginDisposableMemory<LauncherPathEntry>.Alloc();
        entries[0].Write(_background, Span<byte>.Empty);
        handle = entries.AsSafePointer();
        count = 1;
        disposable = entries.IsDisposable == 1;
        allocated = true;
    }

    public override void GetLogoOverlayEntries(out nint handle, out int count, out bool disposable, out bool allocated)
    {
        handle = 0;
        count = 0;
        disposable = false;
        allocated = false;
    }

    protected override Task<int> InitAsync(CancellationToken token) => InitializeAsync(token);

    internal async Task<int> InitializeAsync(CancellationToken token)
    {
        try
        {
            var json = await _client.GetApiAsync("api/launcher/base/config", token).ConfigureAwait(false);
            string? url = json.GetProperty("launcher_background_img").GetString();
            if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme == "https")
                _background = uri.AbsoluteUri;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException
                                       or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            SharedStatic.InstanceLogger.LogWarning(ex,
                "Cannot refresh the official launcher background; using the bundled URL.");
        }

        return 0;
    }
}
