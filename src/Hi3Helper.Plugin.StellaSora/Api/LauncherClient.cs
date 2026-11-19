using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Utility;
using Hi3Helper.Plugin.StellaSora.Management;
using Hi3Helper.Plugin.StellaSora.Configuration;

namespace Hi3Helper.Plugin.StellaSora.Api;

internal sealed class LauncherClient(HttpClient http, StellaSoraRegion region)
{
    internal HttpClient Http { get; } = http;

    internal static HttpClient CreateHttpClient() => new PluginHttpClientBuilder().AllowRedirections().Create();

    internal static string CreateAuthorization(long unixSeconds, StellaSoraRegion region)
    {
        string head = "{\"game_tag\":\"" + region.GameTag + "\",\"time\":" +
                      unixSeconds.ToString(CultureInfo.InvariantCulture) + ",\"version\":\"" + region.LauncherVersion +
                      "\"}";
        string sign = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(head + region.ApplicationSalt)));
        return "{\"head\":" + head + ",\"sign\":\"" + sign + "\"}";
    }

    internal async Task<JsonElement> GetApiAsync(string path, CancellationToken token)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(new Uri(region.ApiBase), path));
        request.Headers.TryAddWithoutValidation("Authorization",
            CreateAuthorization(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), region));
        request.Headers.UserAgent.ParseAdd(region.LauncherProcessName + "/" + region.LauncherVersion);
        using HttpResponseMessage response =
            await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using JsonDocument json = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
        if (json.RootElement.GetProperty("code").GetInt32() != 200)
            throw new InvalidDataException("The official launcher API returned an error: " + path.Split('?')[0]);
        return json.RootElement.GetProperty("data").Clone();
    }

    internal async Task<GameRelease> GetReleaseAsync(CancellationToken token)
    {
        JsonElement data = await GetApiAsync("api/launcher/game/config", token).ConfigureAwait(false);
        string version = data.GetProperty("game_latest_version").GetString() ?? "";
        string basis = data.GetProperty("game_latest_file_path").GetString() ?? "";
        string executable = data.GetProperty("game_start_exe_name").GetString() ?? "";
        if (!GameVersion.TryParse(version, out GameVersion parsed) || parsed == GameVersion.Empty ||
            string.IsNullOrWhiteSpace(basis) ||
            !executable.Equals(region.ExecutableStem, StringComparison.OrdinalIgnoreCase) &&
            !executable.Equals(region.Executable, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The official release is incomplete or belongs to a different game region.");
        return new(parsed, version, basis);
    }

    internal async Task<GameManifest> GetManifestAsync(GameRelease release, CancellationToken token)
    {
        Task<JsonElement> cdnTask = GetApiAsync("api/launcher/advanced/game/download/cdn", token);
        Task<JsonElement> locatorTask = GetApiAsync("api/launcher/game/config/json?version=" +
                                                    Uri.EscapeDataString(release.VersionText) +
                                                    "&file_path=" + Uri.EscapeDataString(release.Basis), token);
        await Task.WhenAll(cdnTask, locatorTask).ConfigureAwait(false);
        JsonElement locator = await locatorTask.ConfigureAwait(false);
        Uri url = RequireHttps(locator.GetProperty("url").GetString());
        UriBuilder manifestUrl = new(url)
        {
            Query = (url.Query.Length == 0 ? "" : url.Query[1..] + "&") +
                    "nocache=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)
        };
        using HttpRequestMessage request = new(HttpMethod.Get, manifestUrl.Uri);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using HttpResponseMessage response =
            await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using JsonDocument json = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
        JsonElement cdn = await cdnTask.ConfigureAwait(false);
        Uri[] domains = new[] { "primary_cdn", "back_up_cdn" }
            .Select(x => cdn.TryGetProperty(x, out JsonElement value) ? value.GetString() : null)
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(RequireHttps).Distinct().ToArray();
        if (domains.Length == 0) throw new InvalidDataException("The official API returned no download CDN.");
        return GameManifest.Parse(json.RootElement, domains, region.Executable);
    }

    private static Uri RequireHttps(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidDataException("The official resource URL is invalid.");
        return uri;
    }
}
