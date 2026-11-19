namespace Hi3Helper.Plugin.StellaSora.Management;

internal static class SafePaths
{
    internal static string NormalizeManifestPath(string path)
    {
        if (path.StartsWith('/') && !path.StartsWith("//", StringComparison.Ordinal)) path = path[1..];
        path = path.Replace('\\', '/');
        string[] parts = path.Split('/');
        foreach (string part in parts)
        {
            string stem = part.Split('.')[0];
            if (string.IsNullOrWhiteSpace(part) || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.Any(c => c < 32 || "<>:\"|?*".Contains(c)) ||
                new[]
                {
                    "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                    "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
                }.Contains(stem, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("Invalid manifest path: " + path);
        }

        if (parts[0].Equals(StellaSoraConstants.WorkDirectory, StringComparison.OrdinalIgnoreCase) ||
            path.Equals(StellaSoraConstants.ConfigName, StringComparison.OrdinalIgnoreCase) ||
            path.Equals(StellaSoraConstants.ManifestName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Manifest path conflicts with installation metadata.");
        return string.Join('/', parts);
    }

    internal static string ResolveForRead(string root, string relative)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (Path.GetPathRoot(root)?.TrimEnd('\\', '/') == root)
            throw new InvalidOperationException("A drive root cannot be used as the game directory.");
        string result = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The file path escapes the game directory.");
        return result;
    }

    internal static string Resolve(string root, string relative)
    {
        string result = ResolveForRead(root, relative);
        for (string? current = result; current != null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Symbolic links or directory junctions are not supported in the game path: " +
                                      current);
        }

        return result;
    }

    // Read-only scans share validated directory entries. Mutations still use Resolve.
    internal sealed class ReadScope(string root)
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<bool>> _directories =
            new(StringComparer.OrdinalIgnoreCase);

        internal string Resolve(string relative)
        {
            string path = ResolveForRead(root, relative);
            Validate(path);
            for (string? directory = Path.GetDirectoryName(path);
                 directory != null;
                 directory = Path.GetDirectoryName(directory))
                _ = _directories.GetOrAdd(directory, static value => new Lazy<bool>(() => Validate(value))).Value;
            return path;
        }

        private static bool Validate(string path)
        {
            if ((File.Exists(path) || Directory.Exists(path)) &&
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Symbolic links or directory junctions are not supported in the game path: " +
                                      path);
            return true;
        }
    }
}