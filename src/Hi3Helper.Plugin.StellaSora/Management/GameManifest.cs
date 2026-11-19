using System.Globalization;
using System.Text.Json;
using Hi3Helper.Plugin.Core.Management;

namespace Hi3Helper.Plugin.StellaSora.Management;

internal sealed record GameRelease(GameVersion Version, string VersionText, string Basis);

internal sealed record GameFile(string Path, ulong Hash, long Size)
{
    internal string OfficialPath => "/" + Path;
    internal string HashText => Hash.ToString(CultureInfo.InvariantCulture);
    internal string SizeText => Size.ToString(CultureInfo.InvariantCulture);

    internal string CacheName => HashText + "_" + SizeText + "_" + Convert.ToHexStringLower(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path)))[..16] + ".part";
}

internal sealed record GameManifest(string Source, GameFile[] Files, Uri[] Domains)
{
    internal long TotalSize => Files.Aggregate(0L, (sum, item) => checked(sum + item.Size));

    internal static GameManifest Parse(JsonElement json, Uri[] domains, string executable)
    {
        string source = SafePaths.NormalizeManifestPath(json.GetProperty("source").GetString() ?? "");
        List<GameFile> files = [];
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement item in json.GetProperty("file").EnumerateArray())
        {
            string path = SafePaths.NormalizeManifestPath(item.GetProperty("path").GetString() ?? "");
            if (!names.Add(path)) throw new InvalidDataException("Duplicate path in the official manifest: " + path);
            long size = long.Parse(item.GetProperty("size").ToString(), CultureInfo.InvariantCulture);
            ulong hash = ulong.Parse(item.GetProperty("hash").ToString(), CultureInfo.InvariantCulture);
            if (size < 0) throw new InvalidDataException("Negative file size in the manifest.");
            files.Add(new(path, hash, size));
        }

        if (!names.Contains(executable) || files.Count == 0)
            throw new InvalidDataException("The official manifest does not contain the expected game executable.");
        foreach (string name in names)
        {
            string? parent = Path.GetDirectoryName(name.Replace('/', Path.DirectorySeparatorChar));
            while (!string.IsNullOrEmpty(parent))
            {
                if (names.Contains(parent.Replace(Path.DirectorySeparatorChar, '/')))
                    throw new InvalidDataException("Manifest file conflicts with a directory: " + name);
                parent = Path.GetDirectoryName(parent);
            }
        }

        GameManifest result = new(source, files.ToArray(), domains);
        _ = result.TotalSize;
        return result;
    }

    internal Uri GetFileUrl(Uri domain, GameFile file) => new(domain,
        "/" + string.Join('/', (Source + "/" + file.Path).Split('/').Select(Uri.EscapeDataString)));
}