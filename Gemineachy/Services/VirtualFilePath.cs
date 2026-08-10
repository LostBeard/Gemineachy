namespace Gemineachy.Services
{
    /// <summary>
    /// Pure (browser-free, testable) path logic for the unified virtual filesystem. The virtual root
    /// "/" contains one directory per mount (its name); everything below is relative to that mount's
    /// FileSystemDirectoryHandle. Normalizes separators, resolves "." and "..", and rejects traversal
    /// above a mount root - so a tool call can never escape the folders the user actually mounted.
    /// </summary>
    public static class VirtualFilePath
    {
        /// <summary>
        /// Normalize a virtual path into clean segments. Root ("/", "", ".") yields an empty list.
        /// segment[0] (when present) is the mount name; the rest is the path within that mount.
        /// Returns false with <paramref name="error"/> set if the path escapes above the root.
        /// </summary>
        public static bool TryNormalize(string? path, out List<string> segments, out string? error)
        {
            segments = new List<string>();
            error = null;
            if (string.IsNullOrWhiteSpace(path)) return true; // root
            foreach (var raw in path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var seg = raw.Trim();
                if (seg.Length == 0 || seg == ".") continue;
                if (seg == "..")
                {
                    if (segments.Count == 0)
                    {
                        error = $"Path '{path}' escapes above the filesystem root.";
                        return false;
                    }
                    segments.RemoveAt(segments.Count - 1);
                    continue;
                }
                segments.Add(seg);
            }
            return true;
        }

        /// <summary>Canonical "/"-joined form of normalized segments (leading slash; root = "/").</summary>
        public static string ToDisplay(IReadOnlyList<string> segments) =>
            segments.Count == 0 ? "/" : "/" + string.Join("/", segments);

        /// <summary>True for the virtual root (no segments) - the level that lists mounts.</summary>
        public static bool IsRoot(IReadOnlyList<string> segments) => segments.Count == 0;

        /// <summary>The last segment (file/dir name), or "" at root.</summary>
        public static string Name(IReadOnlyList<string> segments) => segments.Count == 0 ? "" : segments[^1];

        /// <summary>The mount name (first segment), or "" at root.</summary>
        public static string Mount(IReadOnlyList<string> segments) => segments.Count == 0 ? "" : segments[0];

        /// <summary>Segments relative to the mount (everything after the mount name).</summary>
        public static List<string> WithinMount(IReadOnlyList<string> segments) =>
            segments.Count <= 1 ? new List<string>() : segments.Skip(1).ToList();

        /// <summary>Validate a single path component (mount or entry name): no separators, not "."/"..".</summary>
        public static bool IsValidName(string? name) =>
            !string.IsNullOrWhiteSpace(name) && name != "." && name != ".."
            && name.IndexOf('/') < 0 && name.IndexOf('\\') < 0;

        /// <summary>Convert a filename glob to an ANCHORED JS-compatible regex source. `*` matches any run
        /// of characters, `?` matches one; all other regex metacharacters are escaped literally. Used to
        /// match entry NAMES (not full paths), e.g. "*.md" -> "^.*\.md$".</summary>
        public static string GlobToRegex(string glob)
        {
            var sb = new System.Text.StringBuilder("^");
            foreach (var c in glob ?? "")
            {
                switch (c)
                {
                    case '*': sb.Append(".*"); break;
                    case '?': sb.Append('.'); break;
                    default:
                        if ("\\^$.|+()[]{}".IndexOf(c) >= 0) sb.Append('\\');
                        sb.Append(c);
                        break;
                }
            }
            sb.Append('$');
            return sb.ToString();
        }
    }
}
