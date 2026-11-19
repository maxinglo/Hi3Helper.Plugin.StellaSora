using System.Runtime.InteropServices.Marshalling;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Management.Api;
using Hi3Helper.Plugin.Core.Management.PresetConfig;
using Hi3Helper.Plugin.StellaSora.Api;
using Hi3Helper.Plugin.StellaSora.Configuration;
using Hi3Helper.Plugin.StellaSora.Management;

namespace Hi3Helper.Plugin.StellaSora;

[GeneratedComClass]
internal partial class StellaSoraPresetConfig(StellaSoraRegion region) : PluginPresetConfigBase
{
    internal StellaSoraRegion Region { get; } = region;
    public override string GameName => StellaSoraPlugin.IsChinese ? "星塔旅人" : "Stella Sora";
    public override string GameExecutableName => Region.Executable;

    public override string GameAppDataPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "Low", Region.Vendor,
            Region.AppDataDirectory);

    public override string GameLogFileName => "Player.log";
    public override string GameVendorName => Region.Vendor;
    public override string GameRegistryKeyName => Region.AppDataDirectory;
    public override string ProfileName => "StellaSora" + Region.Id;
    public override string ZoneName => StellaSoraPlugin.IsChinese ? Region.ChineseZoneName : Region.EnglishZoneName;
    public override string ZoneFullName => GameName + " - " + ZoneName;
    public override string ZoneDescription => ZoneFullName;
    public override string ZoneLogoUrl => StellaSoraConstants.Icon;
    public override string ZonePosterUrl => Region.Background;
    public override string ZoneHomePageUrl => Region.Website;
    public override GameReleaseChannel ReleaseChannel => GameReleaseChannel.Public;
    public override string GameMainLanguage => Region.MainLanguage;
    public override string LauncherGameDirectoryName => Region.DirectoryName;
    public override List<string> SupportedLanguages => [.. Region.SupportedLanguages];

    public override ILauncherApiMedia? LauncherApiMedia
    {
        get => field ??= new StellaSoraLauncherMedia(Region);
        set;
    }

    public override ILauncherApiNews? LauncherApiNews
    {
        get => field ??= new StellaSoraLauncherNews(Region);
        set;
    }

    public override IGameManager? GameManager
    {
        get => field ??= new StellaSoraGameManager(Region);
        set;
    }

    public override IGameInstaller? GameInstaller
    {
        get => field ??= new StellaSoraGameInstaller((StellaSoraGameManager)GameManager!);
        set;
    }

    protected override Task<int> InitAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _ = GameManager;
        _ = GameInstaller;
        _ = LauncherApiNews;
        _ = LauncherApiMedia;
        return Task.FromResult(0);
    }

    public override void Dispose()
    {
        GameInstaller?.Free();
        GameInstaller = null;
        base.Dispose();
    }
}