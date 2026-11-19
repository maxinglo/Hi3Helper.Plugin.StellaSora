using System.Globalization;
using System.Net;
using System.Runtime.InteropServices.Marshalling;
using System.Text.Json;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Management.Api;
using Hi3Helper.Plugin.StellaSora.Configuration;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.Plugin.StellaSora.Api;

[GeneratedComClass]
internal sealed partial class StellaSoraLauncherNews : LauncherApiNewsBase, IDisposable
{
    private readonly LauncherClient _client;
    internal NewsItem[] News { get; private set; } = [];
    internal BannerItem[] Banners { get; private set; } = [];
    internal SocialItem[] Socials { get; private set; } = [];

    internal sealed record NewsItem(
        string Title,
        string Description,
        string Url,
        string Date,
        LauncherNewsEntryType Type);

    internal sealed record BannerItem(string Image, string Url, string Description);

    internal sealed record SocialItem(string Name, string Url, string Qr);

    internal StellaSoraLauncherNews(StellaSoraRegion region, HttpClient? http = null)
    {
        ApiResponseHttpClient = http ?? LauncherClient.CreateHttpClient();
        _client = new(ApiResponseHttpClient, region);
    }

    protected override HttpClient ApiResponseHttpClient { get; set; }
    protected override Task<int> InitAsync(CancellationToken token) => InitializeAsync(token);

    internal async Task<int> InitializeAsync(CancellationToken token)
    {
        await Task.WhenAll(RefreshOperationsAsync(token), RefreshSocialAsync(token)).ConfigureAwait(false);
        return 0;
    }

    private async Task RefreshOperationsAsync(CancellationToken token)
    {
        try
        {
            ParseOperations(await _client.GetApiAsync("api/launcher/operations/resource", token).ConfigureAwait(false),
                DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or JsonException or KeyNotFoundException
                                       or InvalidOperationException)
        {
            SharedStatic.InstanceLogger.LogWarning(ex, "Cannot refresh official news and banners.");
        }
    }

    private async Task RefreshSocialAsync(CancellationToken token)
    {
        try
        {
            JsonElement data = await _client.GetApiAsync("api/launcher/social/media/resource", token)
                .ConfigureAwait(false);
            Socials = data.GetProperty("social_media_resource_open").GetBoolean()
                ? data.GetProperty("social_media_resource_list").EnumerateArray()
                    .Select(x => new SocialItem(Text(x, "social_media_channel"), WebUrl(Text(x, "jump_url")),
                        WebUrl(Text(x, "qr_img"))))
                    .Where(x => x.Url.Length > 0 || x.Qr.Length > 0).ToArray()
                : [];
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or JsonException or KeyNotFoundException
                                       or InvalidOperationException)
        {
            SharedStatic.InstanceLogger.LogWarning(ex, "Cannot refresh official social media entries.");
        }
    }

    internal void ParseOperations(JsonElement data, DateTimeOffset now)
    {
        if (!data.GetProperty("operations_resource_open").GetBoolean())
        {
            News = [];
            Banners = [];
            return;
        }

        List<NewsItem> news = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonElement group in NewsGroups(data).OrderBy(group => IsLatest(Text(group, "typeLabel"))))
        {
            LauncherNewsEntryType type = Classify(Text(group, "typeLabel"));
            foreach (JsonElement row in group.GetProperty("rows").EnumerateArray())
            {
                string url = WebUrl(Text(row, "link"));
                if (url.Length == 0 || !seen.Add(url)) continue;
                string date = DateTimeOffset.FromUnixTimeMilliseconds(row.GetProperty("publishTime").GetInt64())
                    .ToLocalTime().ToString("MM/dd", CultureInfo.InvariantCulture);
                news.Add(new(WebUtility.HtmlDecode(Text(row, "title")), WebUtility.HtmlDecode(Text(row, "description")),
                    url, date, type));
            }
        }

        if (data.TryGetProperty("notice_list", out JsonElement notices) && notices.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement group in notices.EnumerateArray().OrderBy(group => IsLatest(Text(group, "notice_type"))))
            {
                LauncherNewsEntryType type = Classify(Text(group, "notice_type"));
                foreach (JsonElement row in group.GetProperty("notice_detail_list").EnumerateArray())
                {
                    string url = WebUrl(Text(row, "jump_url"));
                    if (url.Length == 0 || !seen.Add(url)) continue;
                    string text = Text(row, "notice_time");
                    string date = DateOnly.TryParseExact(text, "yyyy/MM/dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out DateOnly day)
                        ? day.ToString("MM/dd", CultureInfo.InvariantCulture) : text;
                    news.Add(new(WebUtility.HtmlDecode(Text(row, "notice_title")), "", url, date, type));
                }
            }
        }

        News = news.ToArray();
        long seconds = now.ToUnixTimeSeconds();
        Banners = data.GetProperty("operations_banner_list").EnumerateArray()
            .Where(x => (!x.TryGetProperty("start_at", out var start) || start.GetInt64() <= seconds) &&
                        (!x.TryGetProperty("end_at", out var end) || end.GetInt64() == 0 || end.GetInt64() >= seconds))
            .Select(x => new BannerItem(WebUrl(Text(x, "banner_img")), WebUrl(Text(x, "jump_url")), Text(x, "remark")))
            .Where(x => x.Image.Length > 0).ToArray();
    }

    private static IEnumerable<JsonElement> NewsGroups(JsonElement data)
    {
        if (data.TryGetProperty("news_list", out JsonElement list) && list.ValueKind == JsonValueKind.Object &&
            list.TryGetProperty("data", out JsonElement content) && content.ValueKind == JsonValueKind.Object &&
            content.TryGetProperty("news", out JsonElement news) && news.ValueKind == JsonValueKind.Array)
            return news.EnumerateArray();
        return [];
    }

    private static bool IsLatest(string label) => label.Trim().ToLowerInvariant() is
        "最新" or "すべて" or "전체" or "최신" or "latest" or "all";

    private static LauncherNewsEntryType Classify(string label) => label.Trim().ToLowerInvariant() switch
    {
        "公告" or "维护" or "維護" or "お知らせ" or "メンテナンス" or "공지" or "공지사항" or "공지 사항" or "점검" or
            "notice" or "notices" or "announcement" or "announcements" or "maintenance" => LauncherNewsEntryType.Notice,
        "活动" or "活動" or "イベント" or "이벤트" or "이벤트 공지" or "event" or "events" => LauncherNewsEntryType.Event,
        _ => LauncherNewsEntryType.Info
    };

    private static string Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out JsonElement value) ? value.GetString() ?? "" : "";

    private static string WebUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && uri.Scheme is "https" or "http" ? uri.AbsoluteUri : "";

    public override void GetNewsEntries(out nint handle, out int count, out bool disposable, out bool allocated)
    {
        var items = News;
        var memory = items.Length == 0
            ? PluginDisposableMemory<LauncherNewsEntry>.Empty
            : PluginDisposableMemory<LauncherNewsEntry>.Alloc(items.Length);
        for (int i = 0; i < items.Length; i++)
            memory[i].Write(items[i].Title, items[i].Description, items[i].Url, items[i].Date, items[i].Type);
        handle = memory.AsSafePointer();
        count = items.Length;
        disposable = memory.IsDisposable == 1;
        allocated = items.Length > 0;
    }

    public override void GetCarouselEntries(out nint handle, out int count, out bool disposable, out bool allocated)
    {
        var items = Banners;
        var memory = items.Length == 0
            ? PluginDisposableMemory<LauncherCarouselEntry>.Empty
            : PluginDisposableMemory<LauncherCarouselEntry>.Alloc(items.Length);
        for (int i = 0; i < items.Length; i++) memory[i].Write(items[i].Description, items[i].Image, items[i].Url);
        handle = memory.AsSafePointer();
        count = items.Length;
        disposable = memory.IsDisposable == 1;
        allocated = items.Length > 0;
    }

    public override void GetSocialMediaEntries(out nint handle, out int count, out bool disposable, out bool allocated)
    {
        var items = Socials;
        var memory = items.Length == 0
            ? PluginDisposableMemory<LauncherSocialMediaEntry>.Empty
            : PluginDisposableMemory<LauncherSocialMediaEntry>.Alloc(items.Length);
        for (int i = 0; i < items.Length; i++)
        {
            SocialIconCatalog.IconPair icon = SocialIconCatalog.Get(items[i].Name);
            memory[i].WriteDescription(items[i].Name);
            memory[i].WriteIcon(icon.Normal);
            memory[i].WriteIconHover(icon.Hover);
            memory[i].WriteClickUrl(items[i].Url);
            LauncherSocialMediaEntryFlag flags = LauncherSocialMediaEntryFlag.IconIsDataBuffer |
                                                 LauncherSocialMediaEntryFlag.IconHoverIsDataBuffer;
            if (items[i].Qr.Length > 0)
            {
                memory[i].WriteQrImage(items[i].Qr);
                memory[i].WriteQrImageDescription(items[i].Name);
                flags |= LauncherSocialMediaEntryFlag.QrImageIsPath;
            }

            memory[i].Flags = flags;
        }

        handle = memory.AsSafePointer();
        count = items.Length;
        disposable = memory.IsDisposable == 1;
        allocated = items.Length > 0;
    }
}
