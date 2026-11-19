using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.StellaSora.Configuration;

namespace Hi3Helper.Plugin.StellaSora.Management;

internal static class InstalledMetadata
{
    internal const string PendingName = StellaSoraConstants.WorkDirectory + "/pending.json";

    internal static void EnsureRegion(string root, StellaSoraRegion region)
    {
        CheckTag(StellaSoraConstants.ConfigName, "tag");
        CheckTag(StellaSoraConstants.ManifestName, "name");
        CheckTag(PendingName, "tag");

        void CheckTag(string name, string field)
        {
            string path = SafePaths.Resolve(root, name);
            if (!File.Exists(path)) return;
            try
            {
                using JsonDocument json = JsonDocument.Parse(File.ReadAllBytes(path));
                if (json.RootElement.ValueKind == JsonValueKind.Object &&
                    json.RootElement.TryGetProperty(field, out JsonElement value) &&
                    value.ValueKind == JsonValueKind.String &&
                    StellaSoraRegion.Supported.Any(candidate => candidate.Id != region.Id && candidate.GameTag == value.GetString()))
                    throw new InvalidOperationException("This directory belongs to another Stella Sora region.");
            }
            catch (JsonException)
            {
                // Repair may replace damaged metadata.
            }
        }
    }

    internal static string ObjectHash(IEnumerable<string> values) =>
        Convert.ToBase64String(MD5.HashData(Encoding.UTF8.GetBytes(string.Join(';', values))));

    private static bool IsValid(JsonElement item, params string[] excluded)
    {
        if (!item.TryGetProperty("vc", out JsonElement vc)) return false;
        List<string> values = [];
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (JsonProperty property in item.EnumerateObject())
        {
            if (!keys.Add(property.Name)) return false;
            if (property.Name == "vc" || excluded.Contains(property.Name)) continue;
            if (property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            {
                values.Add(property.Value.ToString());
            }
            else if (property.Name == "params" && property.Value.ValueKind == JsonValueKind.Array &&
                     property.Value.EnumerateArray().All(value => value.ValueKind == JsonValueKind.String))
            {
                values.Add(string.Join(',', property.Value.EnumerateArray().Select(value => value.GetString())));
            }
            else
            {
                return false;
            }
        }

        return vc.GetString() == ObjectHash(values);
    }

    internal static GameVersion ReadVersion(string root, StellaSoraRegion region)
    {
        try
        {
            if (File.Exists(SafePaths.Resolve(root, PendingName)))
                return GameVersion.Empty;
            using JsonDocument json =
                JsonDocument.Parse(File.ReadAllBytes(SafePaths.Resolve(root, StellaSoraConstants.ConfigName)));
            JsonElement config = json.RootElement;
            if (!IsValid(config) || config.GetProperty("tag").GetString() != region.GameTag ||
                !string.Equals(config.GetProperty("name").GetString(), region.ExecutableStem, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(SafePaths.Resolve(root, region.Executable))) return GameVersion.Empty;
            return GameVersion.TryParse(config.GetProperty("version").GetString(), out GameVersion version)
                ? version
                : GameVersion.Empty;
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or FormatException
                                       or InvalidOperationException or UnauthorizedAccessException)
        {
            return GameVersion.Empty;
        }
    }

    internal static GameFile[] ReadFiles(string root, StellaSoraRegion region)
    {
        try
        {
            using JsonDocument json =
                JsonDocument.Parse(File.ReadAllBytes(SafePaths.Resolve(root, StellaSoraConstants.ManifestName)));
            JsonElement data = json.RootElement;
            if (!IsValid(data, "files") || data.GetProperty("name").GetString() != region.GameTag) return [];
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            List<GameFile> result = [];
            foreach (JsonElement item in data.GetProperty("files").EnumerateArray())
            {
                if (!IsValid(item)) return [];
                string path = SafePaths.NormalizeManifestPath(item.GetProperty("path").GetString() ?? "");
                long size = long.Parse(item.GetProperty("size").ToString(), CultureInfo.InvariantCulture);
                ulong hash = ulong.Parse(item.GetProperty("hash").ToString(), CultureInfo.InvariantCulture);
                if (size < 0 || !seen.Add(path)) return [];
                result.Add(new(path, hash, size));
            }

            return result.ToArray();
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or FormatException
                                       or OverflowException or InvalidOperationException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    internal static void Commit(string root, StellaSoraRegion region, GameRelease release, GameManifest manifest)
    {
        WriteAtomic(root, StellaSoraConstants.ManifestName, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("name", region.GameTag);
            writer.WriteString("version", release.VersionText);
            writer.WriteString("basis", release.Basis);
            writer.WriteString("vc", ObjectHash([region.GameTag, release.VersionText, release.Basis]));
            writer.WriteStartArray("files");
            foreach (GameFile file in manifest.Files)
            {
                writer.WriteStartObject();
                writer.WriteString("path", file.OfficialPath);
                writer.WriteString("hash", file.HashText);
                writer.WriteString("size", file.SizeText);
                writer.WriteString("vc", ObjectHash([file.OfficialPath, file.HashText, file.SizeText]));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
        WriteAtomic(root, StellaSoraConstants.ConfigName, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("tag", region.GameTag);
            writer.WriteString("name", region.ExecutableStem);
            if (region.HasExtendedInstallConfig)
            {
                writer.WriteStartArray("params");
                writer.WriteEndArray();
            }
            writer.WriteString("version", release.VersionText);
            if (region.HasExtendedInstallConfig) writer.WriteString("gameUninstallScript", "");
            writer.WriteString("vc", ObjectHash(region.HasExtendedInstallConfig
                ? [region.GameTag, region.ExecutableStem, "", release.VersionText, ""]
                : [region.GameTag, region.ExecutableStem, release.VersionText]));
            writer.WriteEndObject();
        });
    }

    internal static void WriteAtomic(string root, string name, Action<Utf8JsonWriter> write)
    {
        string work = SafePaths.Resolve(root, StellaSoraConstants.WorkDirectory);
        Directory.CreateDirectory(work);
        string temporary = SafePaths.Resolve(root,
            StellaSoraConstants.WorkDirectory + "/metadata-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using Utf8JsonWriter writer = new(stream, new() { Indented = true });
                write(writer);
                writer.Flush();
                stream.Flush(true);
            }

            File.Move(temporary, SafePaths.Resolve(root, name), true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
