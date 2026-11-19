namespace Hi3Helper.Plugin.StellaSora.Api;

internal static class SocialIconCatalog
{
    internal sealed record IconPair(byte[] Normal, byte[] Hover);

    private static readonly Dictionary<string, IconPair> Icons = new(StringComparer.Ordinal)
    {
        ["website"] = Load("website"), ["wechat"] = Load("wechat"), ["bilibili"] = Load("bilibili"),
        ["weibo"] = Load("weibo"), ["douyin"] = Load("douyin"), ["customer"] = Load("customer"),
        ["x"] = Load("x"), ["youtube"] = Load("youtube"), ["facebook"] = Load("facebook"),
        ["discord"] = Load("discord"), ["lounge"] = Load("lounge")
    };

    internal static IconPair Get(string channel) => Icons[channel.Trim().ToLowerInvariant() switch
    {
        "微信公众号" or "微信" or "wechat" => "wechat",
        "bilibili" or "b站" => "bilibili",
        "微博" or "weibo" => "weibo",
        "抖音" or "douyin" or "tiktok" => "douyin",
        "联系客服" or "客服" or "customer support" => "customer",
        "x" or "twitter" => "x",
        "youtube" => "youtube",
        "fb" or "facebook" => "facebook",
        "discord" => "discord",
        "lounge" or "naver" or "cafe" => "lounge",
        _ => "website"
    }];

    private static IconPair Load(string name) => new(Read(name), Read(name + "-hover"));

    private static byte[] Read(string name)
    {
        using Stream stream = typeof(SocialIconCatalog).Assembly.GetManifestResourceStream(
                                  "Hi3Helper.Plugin.StellaSora.Assets.Social." + name + ".png")
                              ?? throw new InvalidOperationException("Missing embedded social icon: " + name);
        using MemoryStream output = new();
        stream.CopyTo(output);
        return output.ToArray();
    }
}
