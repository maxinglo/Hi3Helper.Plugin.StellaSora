using System.Runtime.InteropServices.Marshalling;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management.PresetConfig;
using Hi3Helper.Plugin.Core.Update;
using Hi3Helper.Plugin.Core.Utility;
using Hi3Helper.Plugin.StellaSora.Configuration;

namespace Hi3Helper.Plugin.StellaSora;

[GeneratedComClass]
public partial class StellaSoraPlugin : PluginBase
{
    private static DateTime _creationDate = new(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);

    private readonly IPluginPresetConfig[] _presets = StellaSoraRegion.Supported
        .Select(region => (IPluginPresetConfig)new StellaSoraPresetConfig(region)).ToArray();

    internal static bool IsChinese =>
        SharedStatic.PluginLocaleCode?.StartsWith("zh", StringComparison.OrdinalIgnoreCase) != false;

    public override void GetPluginName(out string result) => result = IsChinese ? "星塔旅人" : "Stella Sora";

    public override void GetPluginDescription(out string result) => result = IsChinese
        ? "为 Collapse Launcher 提供星塔旅人的下载、更新和启动功能。"
        : "Install, update, and launch Stella Sora with Collapse Launcher.";

    public override void GetPluginAuthor(out string result) => result = "Maxing";
    public override void GetPluginAppIconUrl(out string result) => result = StellaSoraConstants.Icon;
    public override void GetNotificationPosterUrl(out string result) => result = StellaSoraRegion.Cn.Background;
    public override void GetPluginSelfUpdater(out IPluginSelfUpdate? result) => result = null;
    public override unsafe void GetPluginCreationDate(out DateTime* result) => result = _creationDate.AsPointer();
    public override void GetPresetConfigCount(out int count) => count = _presets.Length;

    public override void GetPresetConfig(int index, out IPluginPresetConfig result) => result =
        index >= 0 && index < _presets.Length ? _presets[index] : throw new ArgumentOutOfRangeException(nameof(index));
}
