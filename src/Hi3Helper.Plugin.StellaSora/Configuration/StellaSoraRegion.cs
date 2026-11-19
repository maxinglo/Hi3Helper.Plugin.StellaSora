namespace Hi3Helper.Plugin.StellaSora.Configuration;

internal sealed record StellaSoraRegion(
    string Id,
    string GameTag,
    string ApiBase,
    string LauncherVersion,
    string ApplicationSalt,
    string Executable,
    string DirectoryName,
    string Website,
    string Background,
    string Vendor,
    string AppDataDirectory,
    string ChineseZoneName,
    string EnglishZoneName,
    string MainLanguage,
    IReadOnlyList<string> SupportedLanguages)
{
    internal string ExecutableStem => Path.GetFileNameWithoutExtension(Executable);
    internal string InstallParentDirectory { get; init; } = "YostarGames";
    internal string LauncherProcessName => GameTag + "_Gamelauncher";
    internal bool HasExtendedInstallConfig { get; init; }

    internal static StellaSoraRegion Cn { get; } = new(
        "CN", "StellaSora_CN", "https://launcher-api.yostar.net/", "1.3.0",
        "872550AD59A235662C5B7D5F88CEBE4B", "xtlr.exe", "StellaSora_CN",
        "https://stellasora.yostar.cn/",
        "https://game-launcher-ss-cn.yostar.net/launcher_background_img/b462b71611807b7d26c3b2cb9f582b53.jpg",
        "RoamingStar", "xtlr", "中国大陆", "Mainland China", "zh-CN", ["zh-CN"]);

    internal static StellaSoraRegion En { get; } = new(
        "EN", "StellaSora_EN", "https://api-launcher-en.yo-star.com/", "1.6.0",
        "DE7108E9B2842FD460F4777702727869", "StellaSora.exe", "StellaSora_EN",
        "https://stellasora.global/",
        "https://launcher-pkg-ss-en.yo-star.com/prod/StellaSora_EN/launcher_background_img/d6ef5b7a1069be8d0e0819775a81d5db.png",
        "YOSTAR (HONG KONG) LIMITED", "StellaSora", "国际服", "Global", "en-US", ["en-US"]);

    internal static StellaSoraRegion Jp { get; } = new(
        "JP", "StellaSora_JP", "https://api-launcher-jp.yo-star.com/", "1.6.0",
        "DE7108E9B2842FD460F4777702727869", "StellaSora.exe", "StellaSora_JP",
        "https://stellasora.jp/",
        "https://launcher-pkg-ss-jp.yo-star.com/prod/StellaSora_JP/launcher_background_img/b33cc2301aa15b9944cf8863f7fb9864.jpg",
        "Yostar, Inc.", "StellaSora", "日服", "Japan", "ja-JP", ["ja-JP"]);

    internal static StellaSoraRegion Kr { get; } = new(
        "KR", "StellaSora_KR", "https://api-launcher-kr.yo-star.com/", "1.6.0",
        "DE7108E9B2842FD460F4777702727869", "StellaSora.exe", "StellaSora_KR",
        "https://stellasora.kr/",
        "https://launcher-pkg-ss-kr.yo-star.com/prod/StellaSora_KR/launcher_background_img/ecd89d571ee22c71e82ed8e6ed9ffd91.png",
        "YOSTAR (HONG KONG) LIMITED", "StellaSora", "韩服", "Korea", "ko-KR", ["ko-KR"]);

    internal static StellaSoraRegion Tw { get; } = new(
        "TW", "StellaSora_TW", "https://api-launcher-tw.stargazer-games.com/", "1.8.2",
        "DE7108E9B2842FD460F4777702727869", "StellaSora.exe", "StellaSora_TW",
        "https://stellasora.stargazer-games.com/",
        "https://launcher-pkg-ss-hk.stargazer-games.com/prod/StellaSora_TW/launcher_background_img/d2c7391cfc1c62b923df9e9c529a9d3c.jpg",
        "Stargazer HongKong Limited", "StellaSora", "台港澳繁中服", "Taiwan, Hong Kong and Macau", "zh-TW", ["zh-TW"])
    {
        InstallParentDirectory = "StargazerGames",
        HasExtendedInstallConfig = true
    };

    internal static IReadOnlyList<StellaSoraRegion> Supported { get; } = [Cn, En, Jp, Kr, Tw];
}
