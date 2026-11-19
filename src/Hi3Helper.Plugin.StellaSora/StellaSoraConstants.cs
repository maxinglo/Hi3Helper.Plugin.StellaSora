namespace Hi3Helper.Plugin.StellaSora;

internal static class StellaSoraConstants
{
    internal const string ConfigName = "game-launcher-config.json";
    internal const string ManifestName = "manifest.json";
    internal const string WorkDirectory = ".collapse-stellasora";

    internal static readonly string Icon = LoadIcon();

    private static string LoadIcon()
    {
        using Stream input = typeof(StellaSoraConstants).Assembly.GetManifestResourceStream(
            "Hi3Helper.Plugin.StellaSora.Assets.icon.ico")!;
        using MemoryStream buffer = new();
        input.CopyTo(buffer);
        return Convert.ToBase64String(buffer.ToArray());
    }
}