using Hi3Helper.Plugin.Core.Update;
using Hi3Helper.Plugin.StellaSora.Api;
using System.Runtime.InteropServices.Marshalling;

namespace Hi3Helper.Plugin.StellaSora;

[GeneratedComClass]
internal sealed partial class StellaSoraPluginSelfUpdater : PluginSelfUpdateBase
{
    private static readonly string[] ReleaseSources =
    [
        "https://github.com/maxinglo/Hi3Helper.Plugin.StellaSora/releases/download/collapse-update/",
        "https://gitee.com/maxing_long/hi3helper-plugin-stella-sora/releases/download/collapse-update/"
    ];

    protected override ReadOnlySpan<string> BaseCdnUrlSpan => ReleaseSources;
    protected override HttpClient UpdateHttpClient => field ??= LauncherClient.CreateHttpClient();
}
