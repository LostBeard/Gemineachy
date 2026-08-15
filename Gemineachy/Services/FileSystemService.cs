using SpawnDev;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;
using JSString = SpawnDev.SpawnJS.JSObjects.String;   // JS String (held JS-side), vs System.String

namespace Gemineachy.Services
{
    /// <summary>
    /// A single mounted directory exposed under the virtual root as "/{Name}".
    /// </summary>
    public class FsMount
    {
        public string Name { get; set; } = "";
        /// <summary>"opfs" (origin private FS, always granted, re-acquirable) or "folder" (user-picked).</summary>
        public string Kind { get; set; } = "folder";
        public FileSystemDirectoryHandle Handle { get; set; } = default!;
        /// <summary>True when this mount's access is saved to IndexedDB and restored on reload.</summary>
        public bool Persist { get; set; }
        /// <summary>Cached permission: "", "r", or "rw". Refreshed on add/grant/load.</summary>
        public string Permission { get; set; } = "";
    }

    /// <summary>
    /// A unified async virtual filesystem for the agent. The virtual root "/" contains one directory per
    /// mount; each mount wraps a browser <see cref="FileSystemDirectoryHandle"/> - either the OPFS root
    /// (<c>navigator.storage.getDirectory()</c>) or a user-picked folder (<c>showDirectoryPicker()</c>).
    /// Folder handles can be persisted to IndexedDB (restored on reload; re-grant needs a user gesture).
    ///
    /// Its tools register with <see cref="GeminiChatService"/> at startup so they are always available;
    /// the agent navigates on demand (list -> read/write) rather than being handed the whole tree. A
    /// shared mounted folder can act as a DevComms channel between the user, this agent, and Gemini.
    /// </summary>
    public class FileSystemService : IAsyncBackgroundService
    {
        public Task Ready => _ready ??= InitAsync();
        private Task? _ready;

        private readonly SpawnJSRuntime _js;
        private readonly GeminiChatService _gemini;
        private readonly List<FsMount> _mounts = new();

        private const string DB_NAME = "gemineachy_fs";
        private const string HANDLES_STORE = "handles"; // name -> FileSystemHandle (folder mounts)
        private const string META_STORE = "meta";       // name -> kind string (presence = persisted)

        /// <summary>Fires when the mount set, a mount's permission, or the access log changes (UI subscribes).</summary>
        public event Action? OnChanged;

        /// <summary>One agent filesystem access, for the Files app's "recent activity" view.</summary>
        public record FsAccess(DateTime Time, string Op, string Path);
        private readonly List<FsAccess> _access = new();
        private const int AccessLogCap = 50;
        /// <summary>Most-recent-first log of the agent's filesystem operations (capped).</summary>
        public IReadOnlyList<FsAccess> RecentAccess => _access;
        /// <summary>The most recent access, or null.</summary>
        public FsAccess? LastAccess => _access.Count > 0 ? _access[0] : null;

        private void LogAccess(string op, string path)
        {
            _access.Insert(0, new FsAccess(DateTime.Now, op, path));
            if (_access.Count > AccessLogCap) _access.RemoveAt(_access.Count - 1);
            NotifyChanged();
        }

        public FileSystemService(SpawnJSRuntime js, GeminiChatService gemini)
        {
            _js = js;
            _gemini = gemini;
        }

        public IReadOnlyList<FsMount> Mounts => _mounts;

        private async Task InitAsync()
        {
            // Register the filesystem tools so they're always available in the manifest index.
            try { _gemini.Register(this); }
            catch (Exception ex) { Console.WriteLine($"[FS] tool register failed: {ex.Message}"); }
            // Restore persisted mounts (permissions come back as "prompt" until a user gesture re-grants).
            try { await RestorePersistedMountsAsync(); }
            catch (Exception ex) { Console.WriteLine($"[FS] restore failed: {ex.Message}"); }
#pragma warning disable CS0162 // unreachable when the flag is off
            if (RunFsSelfTest) { try { await RunFsSelfTestAsync(); } catch (Exception ex) { await ReportSelfTest($"THREW {ex.GetType().Name}: {ex.Message}"); } }
#pragma warning restore CS0162
        }

        // --- Dev self-test (Rule 5c): exercises the real WriteFile/SearchContent/EditFile/ReadFile tool
        //     paths - which read file content JS-side via Blob.TextAsString() (a held JS String) - against
        //     OPFS, with no Gemini dependency, and reports PASS/FAIL to a page DOM attribute so CDP can
        //     read it. This is the path that hung on the OLD SpawnJS; it proves the rebuilt SpawnJS.
        //     PROVEN PASS on SpawnJS 2.0.5 (2026-08-15): search+edit+read all green via the held-String
        //     path. Left dormant (flip to true to re-verify after any SpawnJS/marshaller change). ---------
        private const bool RunFsSelfTest = false;

        private Task ReportSelfTest(string msg)
        {
            Console.WriteLine($"[FSTEST] {msg}");
            try
            {
                using var document = _js.Get<Document>("document");
                using var docEl = document.DocumentElement!;
                docEl.SetAttribute("data-gem-fstest", msg);
            }
            catch (Exception ex) { Console.WriteLine($"[FSTEST] could not write DOM marker: {ex.Message}"); }
            return Task.CompletedTask;
        }

        private async Task RunFsSelfTestAsync()
        {
            // Ensure an OPFS mount to test against (use an existing one, else create it).
            var mount = _mounts.FirstOrDefault(m => m.Kind == "opfs");
            if (mount == null)
            {
                await AddOpfsMountAsync("opfs");
                mount = _mounts.FirstOrDefault(m => m.Kind == "opfs");
            }
            if (mount == null) { await ReportSelfTest("FAIL: no OPFS mount available"); return; }

            var dir = $"/{mount.Name}";
            var path = $"{dir}/__gem_fstest__.txt";
            var content = "alpha line\nBETA has a needle here\ngamma also needle\ndelta clean\n";

            var w = await WriteFile(path, content);
            if (!w.StartsWith("Wrote")) { await ReportSelfTest($"FAIL WriteFile: {w}"); return; }

            // SearchContent + EditFile + ReadFile all read the file JS-side via Blob.TextAsString().
            var s = await SearchContent(dir, "needle", "__gem_fstest__.txt");
            bool searchOk = s.Contains("2 match");

            var e = await EditFile(path, "BETA has a needle here", "BETA replaced");
            bool editOk = e.Contains("replaced 1");

            var r = await ReadFile(path);
            bool readOk = r.Contains("BETA replaced") && r.Contains("gamma also needle");

            await Delete(path);

            var verdict = (searchOk && editOk && readOk) ? "PASS" : "FAIL";
            await ReportSelfTest($"{verdict} search={searchOk} edit={editOk} read={readOk} | search='{Trunc(s)}' edit='{Trunc(e)}'");
        }

        private static string Trunc(string s) => s.Length <= 80 ? s.Replace("\n", "\\n") : s.Substring(0, 80).Replace("\n", "\\n") + "…";

        // ---- Mount management (called from the Files app, always under a user gesture) ----------------

        /// <summary>Mount the origin-private filesystem (OPFS). Always granted; no picker needed.</summary>
        public async Task<string> AddOpfsMountAsync(string name = "opfs")
        {
            name = UniqueName(string.IsNullOrWhiteSpace(name) ? "opfs" : name.Trim());
            var root = await GetOpfsRootAsync();
            if (root == null) return "OPFS is not supported in this browser.";
            var mount = new FsMount { Name = name, Kind = "opfs", Handle = root, Permission = "rw" };
            _mounts.Add(mount);
            NotifyChanged();
            await NotifyGeminiMountAdded(mount);
            return $"Mounted OPFS at /{name}.";
        }

        /// <summary>Pick a local folder and mount it. Persistable to IndexedDB.</summary>
        /// <param name="desiredName">Optional mount name. When empty, the picked folder's own name is used
        /// (a drive root may have none). Either way the name is sanitized and made unique.</param>
        public async Task<string> MountFolderAsync(string? desiredName = null)
        {
            using var window = _js.Get<Window>("window");
            if (!window.ShowDirectoryPickerSupported())
                return "This browser does not support picking folders (showDirectoryPicker).";
            FileSystemDirectoryHandle handle;
            try
            {
                handle = await window.ShowDirectoryPicker(new ShowDirectoryPickerOptions { Mode = "readwrite" });
            }
            catch (Exception ex)
            {
                // User cancelled the picker, or it threw - not an error worth surfacing loudly.
                return $"No folder mounted ({ex.GetType().Name}).";
            }
            if (handle == null) return "No folder was selected.";
            // User-provided name wins; otherwise fall back to the folder's own name (may be empty for a
            // drive root - SanitizeName defaults it). UniqueName then resolves any collision.
            var requested = string.IsNullOrWhiteSpace(desiredName) ? handle.Name : desiredName;
            var name = UniqueName(SanitizeName(requested));
            var mount = new FsMount { Name = name, Kind = "folder", Handle = handle, Permission = await handle.GetReadWritePermissions() };
            _mounts.Add(mount);
            NotifyChanged();
            await NotifyGeminiMountAdded(mount);
            return $"Mounted folder '{handle.Name}' at /{name}.";
        }

        /// <summary>Remove a mount and its persisted access.</summary>
        public async Task RemoveMountAsync(string name)
        {
            var mount = FindMount(name);
            if (mount == null) return;
            _mounts.Remove(mount);
            if (mount.Kind != "opfs") mount.Handle?.Dispose();
            try { await DeletePersistedAsync(name); } catch { }
            NotifyChanged();
            await NotifyGeminiMountRemoved(name);
        }

        /// <summary>Turn persistence on/off for a mount (saves/removes the handle in IndexedDB).</summary>
        public async Task SetPersistAsync(string name, bool persist)
        {
            var mount = FindMount(name);
            if (mount == null) return;
            // Let exceptions propagate so the Files app surfaces them (persistence must not fail silently).
            if (persist) await SavePersistedAsync(mount);
            else await DeletePersistedAsync(name);
            mount.Persist = persist;
            NotifyChanged();
        }

        /// <summary>Request read/write access for a mount (must be called from a user gesture).</summary>
        public async Task<bool> GrantAccessAsync(string name)
        {
            var mount = FindMount(name);
            if (mount == null) return false;
            var ok = await mount.Handle.VerifyPermission(readWrite: true, askIfNeeded: true);
            mount.Permission = await mount.Handle.GetReadWritePermissions();
            NotifyChanged();
            return ok;
        }

        /// <summary>Refresh cached permission strings (no prompt) - e.g. when the Files app opens.</summary>
        public async Task RefreshPermissionsAsync()
        {
            foreach (var m in _mounts)
            {
                try { m.Permission = await m.Handle.GetReadWritePermissions(); } catch { }
            }
            NotifyChanged();
        }

        // ---- Agent tools -----------------------------------------------------------------------------

        [AgentTool("List a directory in the virtual filesystem. Path '/' lists the mounted folders; '/mount/sub' lists that folder's entries (name, type, size). The user mounts folders in the Files app; you cannot mount them yourself.")]
        async Task<string> ListDirectory(string path = "/")
        {
            LogAccess("list", path);
            if (!VirtualFilePath.TryNormalize(path, out var segs, out var err)) return err!;
            if (VirtualFilePath.IsRoot(segs))
            {
                if (_mounts.Count == 0)
                    return "No folders are mounted. Ask the user to mount a folder (or enable OPFS) in the Files app; then paths like /mountname/file.txt become available.";
                var lines = _mounts.Select(m => $"/{m.Name}  ({m.Kind}, {(string.IsNullOrEmpty(m.Permission) ? "no access - user must grant" : m.Permission)})");
                return "Mounts:\n" + string.Join("\n", lines) + "\n\nUse ListDirectory(\"/<mount>\") to browse.";
            }
            var (dir, owns, derr) = await ResolveDirectoryAsync(segs, create: false);
            if (dir == null) return derr!;
            try
            {
                var entries = await dir.EntriesList();
                if (entries.Count == 0) return $"{VirtualFilePath.ToDisplay(segs)} is empty.";
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"{VirtualFilePath.ToDisplay(segs)}:");
                foreach (var (childName, handle) in entries.OrderBy(e => e.Item1, StringComparer.OrdinalIgnoreCase))
                {
                    if (handle is FileSystemFileHandle fh)
                    {
                        long size = -1;
                        try { size = await fh.GetSize(); } catch { }
                        sb.AppendLine($"  {childName}\tfile\t{(size >= 0 ? size + " bytes" : "?")}");
                    }
                    else sb.AppendLine($"  {childName}/\tdir");
                    handle.Dispose();
                }
                return sb.ToString().TrimEnd();
            }
            finally { if (owns) dir.Dispose(); }
        }

        [AgentTool("Read a text file from the virtual filesystem, e.g. \"/devcomms/notes.md\". Returns the file's text. Large files above maxBytes are refused (raise maxBytes to read more).")]
        async Task<string> ReadFile(string path, int maxBytes = 65536)
        {
            LogAccess("read", path);
            if (!VirtualFilePath.TryNormalize(path, out var segs, out var err)) return err!;
            if (VirtualFilePath.IsRoot(segs) || segs.Count < 2) return "Provide a file path like /mount/file.txt.";
            var (file, ferr) = await ResolveFileAsync(segs, create: false);
            if (file == null) return ferr!;
            try
            {
                long size = await file.GetSize();
                if (size > maxBytes)
                    return $"File is {size} bytes, larger than maxBytes ({maxBytes}). Raise maxBytes to read it, or read a smaller file.";
                using var f = await file.GetFile();
                return await f.Text();
            }
            catch (Exception ex) { return $"Could not read {VirtualFilePath.ToDisplay(segs)}: {ex.Message}"; }
            finally { file.Dispose(); }
        }

        [AgentTool("Write text to a file in the virtual filesystem (creates it and any parent folders). By default it overwrites; pass append=true to add to the end. The target mount must have write access granted by the user.")]
        async Task<string> WriteFile(string path, string content, bool append = false)
        {
            LogAccess(append ? "append" : "write", path);
            if (!VirtualFilePath.TryNormalize(path, out var segs, out var err)) return err!;
            if (VirtualFilePath.IsRoot(segs) || segs.Count < 2) return "Provide a file path like /mount/file.txt.";
            var mount = FindMount(VirtualFilePath.Mount(segs));
            if (mount == null) return $"No mount named '{VirtualFilePath.Mount(segs)}'. Call ListDirectory(\"/\") for the mounts.";
            if (!await EnsureWritableAsync(mount)) return $"No write access to /{mount.Name}. Ask the user to grant write access in the Files app.";
            var (file, ferr) = await ResolveFileAsync(segs, create: true);
            if (file == null) return ferr!;
            try
            {
                content ??= "";
                if (append)
                {
                    long existing = await file.GetSize();
                    using var ws = await file.CreateWritable(new FileSystemCreateWritableOptions { KeepExistingData = true });
                    await ws.Seek((ulong)existing);
                    await ws.Write(content);
                    await ws.Close();
                }
                else
                {
                    using var ws = await file.CreateWritable();
                    await ws.Truncate(0);
                    await ws.Write(content);
                    await ws.Close();
                }
                return $"Wrote {content.Length} chars to {VirtualFilePath.ToDisplay(segs)}{(append ? " (appended)" : "")}.";
            }
            catch (Exception ex) { return $"Could not write {VirtualFilePath.ToDisplay(segs)}: {ex.Message}"; }
            finally { file.Dispose(); }
        }

        [AgentTool("Create a directory (and any missing parents) in the virtual filesystem, e.g. \"/devcomms/replies\". The target mount must have write access.")]
        async Task<string> MakeDirectory(string path)
        {
            LogAccess("mkdir", path);
            if (!VirtualFilePath.TryNormalize(path, out var segs, out var err)) return err!;
            if (VirtualFilePath.IsRoot(segs)) return "Cannot create the root. Provide a path like /mount/newdir.";
            var mount = FindMount(VirtualFilePath.Mount(segs));
            if (mount == null) return $"No mount named '{VirtualFilePath.Mount(segs)}'. Call ListDirectory(\"/\").";
            if (segs.Count < 2) return "Directories are created inside a mount, e.g. /mount/newdir.";
            if (!await EnsureWritableAsync(mount)) return $"No write access to /{mount.Name}. Ask the user to grant write access in the Files app.";
            var (dir, owns, derr) = await ResolveDirectoryAsync(segs, create: true);
            if (dir == null) return derr!;
            if (owns) dir.Dispose();
            return $"Created directory {VirtualFilePath.ToDisplay(segs)}.";
        }

        [AgentTool("Delete a file or directory from the virtual filesystem. Pass recursive=true to delete a non-empty directory. The target mount must have write access.")]
        async Task<string> Delete(string path, bool recursive = false)
        {
            LogAccess("delete", path);
            if (!VirtualFilePath.TryNormalize(path, out var segs, out var err)) return err!;
            if (VirtualFilePath.IsRoot(segs) || segs.Count < 2) return "Provide the path of an entry inside a mount, e.g. /mount/file.txt.";
            var mount = FindMount(VirtualFilePath.Mount(segs));
            if (mount == null) return $"No mount named '{VirtualFilePath.Mount(segs)}'.";
            if (!await EnsureWritableAsync(mount)) return $"No write access to /{mount.Name}. Ask the user to grant write access in the Files app.";
            var parentSegs = segs.Take(segs.Count - 1).ToList();
            var name = VirtualFilePath.Name(segs);
            var (parent, owns, derr) = await ResolveDirectoryAsync(parentSegs, create: false);
            if (parent == null) return derr!;
            try
            {
                await parent.RemoveEntry(name, recursive);
                return $"Deleted {VirtualFilePath.ToDisplay(segs)}.";
            }
            catch (Exception ex) { return $"Could not delete {VirtualFilePath.ToDisplay(segs)}: {ex.Message} (a non-empty directory needs recursive=true)."; }
            finally { if (owns) parent.Dispose(); }
        }

        [AgentTool("Get info about a path in the virtual filesystem: whether it exists, whether it's a file or directory, and (for files) size and last-modified time.")]
        async Task<string> GetInfo(string path)
        {
            LogAccess("info", path);
            if (!VirtualFilePath.TryNormalize(path, out var segs, out var err)) return err!;
            if (VirtualFilePath.IsRoot(segs)) return $"/ is the virtual root ({_mounts.Count} mount(s)).";
            var mount = FindMount(VirtualFilePath.Mount(segs));
            if (mount == null) return $"No mount named '{VirtualFilePath.Mount(segs)}'.";
            // Try as a directory first, then as a file.
            var (dir, owns, _) = await ResolveDirectoryAsync(segs, create: false);
            if (dir != null)
            {
                if (owns) dir.Dispose();
                return $"{VirtualFilePath.ToDisplay(segs)} is a directory (mount /{mount.Name}, {mount.Permission}).";
            }
            var (file, _) = await ResolveFileAsync(segs, create: false);
            if (file != null)
            {
                try
                {
                    long size = await file.GetSize();
                    long modified = await file.GetLastModified();
                    var when = DateTimeOffset.FromUnixTimeMilliseconds(modified).ToLocalTime();
                    return $"{VirtualFilePath.ToDisplay(segs)} is a file: {size} bytes, modified {when:yyyy-MM-dd HH:mm:ss}.";
                }
                finally { file.Dispose(); }
            }
            return $"{VirtualFilePath.ToDisplay(segs)} does not exist.";
        }

        // ---- QoL search / replace tools (matching runs JS-side; bulk content never enters .NET) -------

        [AgentTool("Search file CONTENTS by regular expression (like grep). Searches text files under `path` recursively, optionally limited to files whose name matches `filePattern` (a glob like \"*.md\"). Returns each matching file with its matching lines. The regex runs in JavaScript on the file text held browser-side - file contents are never copied into .NET.")]
        async Task<string> SearchContent(string path, string pattern, string filePattern = "", bool caseInsensitive = false, int maxResults = 200)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return "Provide a regex pattern to search for.";
            if (!VirtualFilePath.TryNormalize(path, out var segs, out var err)) return err!;
            LogAccess("search", string.IsNullOrEmpty(filePattern) ? path : $"{path} [{filePattern}]");
            RegExp? nameRe;
            RegExp lineRe;
            try
            {
                nameRe = BuildNameRegex(filePattern);
                // Wrap the user's pattern so each match is a whole line ('m' => ^/$ per line; '.' excludes \n).
                lineRe = new RegExp("^.*(?:" + pattern + ").*$", "gm" + (caseInsensitive ? "i" : ""));
            }
            catch (Exception ex) { return $"Invalid regex: {ex.Message}"; }
            var roots = await WalkRootsAsync(segs);
            if (roots.Count == 0) return $"Path '{VirtualFilePath.ToDisplay(segs)}' not found.";
            var sb = new System.Text.StringBuilder();
            int fileHits = 0, total = 0;
            var st = new WalkState { Cap = int.MaxValue };
            foreach (var (baseSegs, dir, owns) in roots)
            {
                await WalkAsync(baseSegs, dir, 0, 64, st, async (fsegs, fh) =>
                {
                    if (nameRe != null && !nameRe.Test(VirtualFilePath.Name(fsegs))) return;
                    JSString? text = null;
                    try
                    {
                        using (var file = await fh.GetFile()) text = await file.TextAsString();
                        using var matches = text.Match(lineRe);
                        int n = matches?.Length ?? 0;
                        if (n == 0) return;
                        int take = Math.Min(n, Math.Max(0, maxResults - total));
                        sb.AppendLine($"{VirtualFilePath.ToDisplay(fsegs)} ({n} match{(n == 1 ? "" : "es")}):");
                        if (take > 0)
                            foreach (var ln in matches!.ToList<string>(0, take)) sb.AppendLine("  " + ln.Trim());
                        fileHits++;
                        total += n;
                        if (total >= maxResults) st.Stopped = true;
                    }
                    catch { }
                    finally { text?.Dispose(); }
                });
                if (owns) dir.Dispose();
            }
            if (fileHits == 0) return $"No matches for /{pattern}/ under {VirtualFilePath.ToDisplay(segs)}.";
            return $"{fileHits} file(s), {total} match(es){(st.Stopped ? $" (shown capped at {maxResults})" : "")}:\n" + sb.ToString().TrimEnd();
        }

        [AgentTool("Find files by NAME (not contents) under `path`, recursively. `namePattern` is a glob like \"*.cs\" or \"tool*.md\". Returns the matching file paths.")]
        async Task<string> FindFiles(string path, string namePattern, int maxResults = 200)
        {
            if (string.IsNullOrWhiteSpace(namePattern)) return "Provide a name pattern (glob), e.g. *.md.";
            if (!VirtualFilePath.TryNormalize(path, out var segs, out var err)) return err!;
            LogAccess("find", $"{path} [{namePattern}]");
            RegExp nameRe;
            try { nameRe = new RegExp(VirtualFilePath.GlobToRegex(namePattern), "i"); }
            catch (Exception ex) { return $"Invalid pattern: {ex.Message}"; }
            var roots = await WalkRootsAsync(segs);
            if (roots.Count == 0) return $"Path '{VirtualFilePath.ToDisplay(segs)}' not found.";
            var found = new List<string>();
            var st = new WalkState { Cap = int.MaxValue };
            foreach (var (baseSegs, dir, owns) in roots)
            {
                await WalkAsync(baseSegs, dir, 0, 64, st, (fsegs, fh) =>
                {
                    if (nameRe.Test(VirtualFilePath.Name(fsegs)))
                    {
                        found.Add(VirtualFilePath.ToDisplay(fsegs));
                        if (found.Count >= maxResults) st.Stopped = true;
                    }
                    return Task.CompletedTask;
                });
                if (owns) dir.Dispose();
            }
            if (found.Count == 0) return $"No files matching '{namePattern}' under {VirtualFilePath.ToDisplay(segs)}.";
            return $"{found.Count} file(s){(st.Stopped ? " (capped)" : "")}:\n" + string.Join("\n", found);
        }

        [AgentTool("Search-and-replace by regular expression across files under `path` recursively, optionally limited to `filePattern` (glob). `replacement` may use $1, $2 for capture groups. Runs as a DRY RUN by default, reporting how many replacements each file would get; pass dryRun=false to actually write (the mount must have write access). Matching and replacement happen in JavaScript - file contents never enter .NET.")]
        async Task<string> ReplaceInFiles(string path, string pattern, string replacement, string filePattern = "", bool caseInsensitive = false, bool dryRun = true)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return "Provide a regex pattern.";
            if (!VirtualFilePath.TryNormalize(path, out var segs, out var err)) return err!;
            LogAccess(dryRun ? "replace(dry)" : "replace", string.IsNullOrEmpty(filePattern) ? path : $"{path} [{filePattern}]");
            RegExp? nameRe;
            RegExp re;
            try { nameRe = BuildNameRegex(filePattern); re = new RegExp(pattern, "g" + (caseInsensitive ? "i" : "")); }
            catch (Exception ex) { return $"Invalid regex: {ex.Message}"; }
            var roots = await WalkRootsAsync(segs);
            if (roots.Count == 0) return $"Path '{VirtualFilePath.ToDisplay(segs)}' not found.";
            var report = new System.Text.StringBuilder();
            int filesChanged = 0, totalRepl = 0;
            var st = new WalkState { Cap = int.MaxValue };
            foreach (var (baseSegs, dir, owns) in roots)
            {
                await WalkAsync(baseSegs, dir, 0, 64, st, async (fsegs, fh) =>
                {
                    if (nameRe != null && !nameRe.Test(VirtualFilePath.Name(fsegs))) return;
                    JSString? text = null;
                    try
                    {
                        using (var file = await fh.GetFile()) text = await file.TextAsString();
                        int n;
                        using (var matches = text.Match(re)) n = matches?.Length ?? 0;
                        if (n == 0) return;
                        totalRepl += n;
                        filesChanged++;
                        report.AppendLine($"  {VirtualFilePath.ToDisplay(fsegs)}: {n} replacement{(n == 1 ? "" : "es")}");
                        if (!dryRun)
                        {
                            var mount = FindMount(VirtualFilePath.Mount(fsegs));
                            if (mount == null || !await EnsureWritableAsync(mount))
                            {
                                report.AppendLine($"    (skipped: no write access to /{mount?.Name})");
                                return;
                            }
                            using var newText = text.Replace(re, replacement ?? "");
                            using var ws = await fh.CreateWritable();
                            await ws.Truncate(0);
                            await ws.Write(newText);   // JS String ref - content stays JS-side
                            await ws.Close();
                        }
                    }
                    catch (Exception ex) { report.AppendLine($"  {VirtualFilePath.ToDisplay(fsegs)}: error - {ex.Message}"); }
                    finally { text?.Dispose(); }
                });
                if (owns) dir.Dispose();
            }
            if (filesChanged == 0) return $"No matches for /{pattern}/ under {VirtualFilePath.ToDisplay(segs)}.";
            var verb = dryRun ? "would change" : "changed";
            return $"{(dryRun ? "DRY RUN - " : "")}{verb} {filesChanged} file(s), {totalRepl} replacement(s):\n"
                 + report.ToString().TrimEnd() + (dryRun ? "\n\nRe-run with dryRun=false to apply." : "");
        }

        [AgentTool("Make a precise edit to ONE text file: replace an EXACT literal substring `oldText` with `newText`. By default `oldText` must occur EXACTLY ONCE (it fails otherwise, so you never edit the wrong place - include enough surrounding context to make it unique); pass replaceAll=true to replace every occurrence. `oldText` is matched literally (NOT a regex) - prefer this over ReplaceInFiles for a single targeted change. The mount must have write access.")]
        async Task<string> EditFile(string path, string oldText, string newText, bool replaceAll = false)
        {
            LogAccess("edit", path);
            if (string.IsNullOrEmpty(oldText)) return "Provide the exact existing text to replace (oldText).";
            if (!VirtualFilePath.TryNormalize(path, out var segs, out var err)) return err!;
            if (VirtualFilePath.IsRoot(segs) || segs.Count < 2) return "Provide a file path like /mount/file.txt.";
            var mount = FindMount(VirtualFilePath.Mount(segs));
            if (mount == null) return $"No mount named '{VirtualFilePath.Mount(segs)}'. Call ListDirectory(\"/\").";
            if (!await EnsureWritableAsync(mount)) return $"No write access to /{mount.Name}. Ask the user to grant write access in the Files app.";
            var (file, ferr) = await ResolveFileAsync(segs, create: false);
            if (file == null) return ferr!;
            JSString? text = null;
            try
            {
                RegExp re;
                try { re = new RegExp(RegexEscape(oldText), "g"); }
                catch (Exception ex) { return $"Could not build a matcher for oldText: {ex.Message}"; }
                using (var f = await file.GetFile()) text = await f.TextAsString(); // content stays JS-side
                int n;
                using (var matches = text.Match(re)) n = matches?.Length ?? 0;
                if (n == 0) return $"oldText was not found in {VirtualFilePath.ToDisplay(segs)}. It must match the file exactly (whitespace and case included).";
                if (n > 1 && !replaceAll) return $"oldText occurs {n} times in {VirtualFilePath.ToDisplay(segs)}. Add surrounding context so it is unique, or pass replaceAll=true to replace all {n}.";
                // Escape '$' so JS treats newText literally (otherwise $1/$&/$$ would expand in the replacement).
                var repl = (newText ?? "").Replace("$", "$$");
                using var updated = text.Replace(re, repl);
                using var ws = await file.CreateWritable();
                await ws.Truncate(0);
                await ws.Write(updated);      // JS String ref - content never enters .NET
                await ws.Close();
                var count = replaceAll ? n : 1;
                return $"Edited {VirtualFilePath.ToDisplay(segs)}: replaced {count} occurrence{(count == 1 ? "" : "s")} of oldText.";
            }
            catch (Exception ex) { return $"Could not edit {VirtualFilePath.ToDisplay(segs)}: {ex.Message}"; }
            finally { text?.Dispose(); file.Dispose(); }
        }

        [AgentTool("Move or rename a text file: copies /from to /to (creating any parent folders) then deletes /from. Overwrites /to if it already exists. Works within a mount or across mounts; both must have write access. (Directories are not moved - move their files individually.)")]
        async Task<string> Move(string fromPath, string toPath)
        {
            LogAccess("move", $"{fromPath} -> {toPath}");
            if (!VirtualFilePath.TryNormalize(fromPath, out var fromSegs, out var e1)) return e1!;
            if (!VirtualFilePath.TryNormalize(toPath, out var toSegs, out var e2)) return e2!;
            if (VirtualFilePath.IsRoot(fromSegs) || fromSegs.Count < 2) return "Provide a source file path like /mount/file.txt.";
            if (VirtualFilePath.IsRoot(toSegs) || toSegs.Count < 2) return "Provide a destination file path like /mount/newname.txt.";
            if (VirtualFilePath.ToDisplay(fromSegs) == VirtualFilePath.ToDisplay(toSegs)) return "Source and destination are the same path.";
            var fromMount = FindMount(VirtualFilePath.Mount(fromSegs));
            var toMount = FindMount(VirtualFilePath.Mount(toSegs));
            if (fromMount == null) return $"No mount named '{VirtualFilePath.Mount(fromSegs)}'.";
            if (toMount == null) return $"No mount named '{VirtualFilePath.Mount(toSegs)}'.";
            if (!await EnsureWritableAsync(fromMount)) return $"No write access to /{fromMount.Name} (needed to remove the source).";
            if (!await EnsureWritableAsync(toMount)) return $"No write access to /{toMount.Name}.";
            var (src, serr) = await ResolveFileAsync(fromSegs, create: false);
            if (src == null) return serr!;
            JSString? text = null;
            try
            {
                using (var f = await src.GetFile()) text = await f.TextAsString(); // content stays JS-side
                var (dst, derr) = await ResolveFileAsync(toSegs, create: true);
                if (dst == null) return derr!;
                try
                {
                    using var ws = await dst.CreateWritable();
                    await ws.Truncate(0);
                    await ws.Write(text);      // JS String ref - content never enters .NET
                    await ws.Close();
                }
                finally { dst.Dispose(); }
                // Remove the source only after the destination write succeeded.
                var parentSegs = fromSegs.Take(fromSegs.Count - 1).ToList();
                var name = VirtualFilePath.Name(fromSegs);
                var (parent, owns, perr) = await ResolveDirectoryAsync(parentSegs, create: false);
                if (parent == null) return $"Copied to {VirtualFilePath.ToDisplay(toSegs)}, but could not remove the source: {perr}";
                try { await parent.RemoveEntry(name, false); }
                finally { if (owns) parent.Dispose(); }
                return $"Moved {VirtualFilePath.ToDisplay(fromSegs)} -> {VirtualFilePath.ToDisplay(toSegs)}.";
            }
            catch (Exception ex) { return $"Could not move {VirtualFilePath.ToDisplay(fromSegs)}: {ex.Message}"; }
            finally { text?.Dispose(); src.Dispose(); }
        }

        [AgentTool("Show a directory tree under `path` to `maxDepth` levels - a compact structural overview. Path '/' shows all mounts.")]
        async Task<string> Tree(string path = "/", int maxDepth = 3, int maxEntries = 300)
        {
            if (!VirtualFilePath.TryNormalize(path, out var segs, out var err)) return err!;
            LogAccess("tree", path);
            var sb = new System.Text.StringBuilder();
            var st = new WalkState { Cap = maxEntries };
            if (VirtualFilePath.IsRoot(segs))
            {
                if (_mounts.Count == 0) return "No folders are mounted.";
                foreach (var m in _mounts)
                {
                    sb.AppendLine($"/{m.Name}");
                    await TreePrintAsync(m.Handle, "  ", 1, maxDepth, sb, st);
                }
            }
            else
            {
                var (dir, owns, derr) = await ResolveDirectoryAsync(segs, false);
                if (dir == null) return derr!;
                sb.AppendLine(VirtualFilePath.ToDisplay(segs));
                try { await TreePrintAsync(dir, "  ", 1, maxDepth, sb, st); }
                finally { if (owns) dir.Dispose(); }
            }
            return sb.ToString().TrimEnd() + (st.Stopped ? "\n… (capped)" : "");
        }

        // ---- Recursive walk helpers ------------------------------------------------------------------

        private class WalkState { public int Count; public int Cap = int.MaxValue; public bool Stopped; }

        /// <summary>The set of directories to start a recursive tool from: every mount at root "/", or the
        /// single resolved directory otherwise. Dispose a returned handle only when its `owns` is true.</summary>
        private async Task<List<(List<string> segs, FileSystemDirectoryHandle dir, bool owns)>> WalkRootsAsync(IReadOnlyList<string> segs)
        {
            var roots = new List<(List<string>, FileSystemDirectoryHandle, bool)>();
            if (VirtualFilePath.IsRoot(segs))
            {
                foreach (var m in _mounts) roots.Add((new List<string> { m.Name }, m.Handle, false));
            }
            else
            {
                var (dir, owns, _) = await ResolveDirectoryAsync(segs, false);
                if (dir != null) roots.Add((segs.ToList(), dir, owns));
            }
            return roots;
        }

        /// <summary>Depth-first recursive walk; calls <paramref name="visitFile"/> for each file. Disposes
        /// every child handle it opens (never the passed-in <paramref name="dir"/>).</summary>
        private async Task WalkAsync(List<string> segs, FileSystemDirectoryHandle dir, int depth, int maxDepth,
            WalkState st, Func<List<string>, FileSystemFileHandle, Task> visitFile)
        {
            if (st.Stopped) return;
            List<(string, FileSystemHandle)> entries;
            try { entries = await dir.EntriesList(); } catch { return; }
            foreach (var (name, handle) in entries.OrderBy(e => e.Item1, StringComparer.OrdinalIgnoreCase))
            {
                if (st.Stopped) { handle.Dispose(); continue; }
                var childSegs = new List<string>(segs) { name };
                if (handle is FileSystemFileHandle fh)
                {
                    try { await visitFile(childSegs, fh); } catch { }
                    fh.Dispose();
                }
                else if (handle is FileSystemDirectoryHandle dh)
                {
                    if (depth + 1 <= maxDepth) await WalkAsync(childSegs, dh, depth + 1, maxDepth, st, visitFile);
                    dh.Dispose();
                }
                else handle.Dispose();
            }
        }

        private async Task TreePrintAsync(FileSystemDirectoryHandle dir, string indent, int depth, int maxDepth,
            System.Text.StringBuilder sb, WalkState st)
        {
            if (st.Stopped || depth > maxDepth) return;
            List<(string, FileSystemHandle)> entries;
            try { entries = await dir.EntriesList(); } catch { return; }
            foreach (var (name, handle) in entries.OrderBy(e => e.Item1, StringComparer.OrdinalIgnoreCase))
            {
                if (st.Stopped) { handle.Dispose(); continue; }
                if (++st.Count > st.Cap) { st.Stopped = true; handle.Dispose(); break; }
                if (handle is FileSystemDirectoryHandle dh)
                {
                    sb.AppendLine($"{indent}{name}/");
                    await TreePrintAsync(dh, indent + "  ", depth + 1, maxDepth, sb, st);
                    dh.Dispose();
                }
                else { sb.AppendLine($"{indent}{name}"); handle.Dispose(); }
            }
        }

        /// <summary>Build a case-insensitive JS RegExp matching entry NAMES from a glob, or null if blank.</summary>
        private static RegExp? BuildNameRegex(string glob) =>
            string.IsNullOrWhiteSpace(glob) ? null : new RegExp(VirtualFilePath.GlobToRegex(glob), "i");

        /// <summary>Escape a literal string so it can be used as a JS RegExp source that matches it verbatim
        /// (used by EditFile to match oldText literally, not as a pattern).</summary>
        private static string RegexEscape(string s) =>
            System.Text.RegularExpressions.Regex.Replace(s, @"[.*+?^${}()|[\]\\]", "\\$&");

        // ---- Path resolution -------------------------------------------------------------------------

        /// <summary>Resolve a directory. Returns (handle, owns). Dispose the handle only when owns is
        /// true (owns is false when the handle is a mount root, which the service keeps).</summary>
        private async Task<(FileSystemDirectoryHandle? dir, bool owns, string? error)> ResolveDirectoryAsync(IReadOnlyList<string> segs, bool create)
        {
            var mount = FindMount(VirtualFilePath.Mount(segs));
            if (mount == null) return (null, false, $"No mount named '{VirtualFilePath.Mount(segs)}'. Call ListDirectory(\"/\") for the mounts.");
            var within = VirtualFilePath.WithinMount(segs);
            if (within.Count == 0) return (mount.Handle, false, null); // the mount root itself
            FileSystemDirectoryHandle current = mount.Handle;
            bool currentOwned = false;
            for (int i = 0; i < within.Count; i++)
            {
                FileSystemDirectoryHandle next;
                try { next = await current.GetDirectoryHandle(within[i], create); }
                catch (Exception ex)
                {
                    if (currentOwned) current.Dispose();
                    return (null, false, $"Directory '{VirtualFilePath.ToDisplay(segs.Take(mountDepth(segs) + i + 1).ToList())}' not found ({ex.GetType().Name}).");
                }
                if (currentOwned) current.Dispose();
                current = next;
                currentOwned = true;
            }
            return (current, currentOwned, null);
        }

        // mount name occupies index 0 of the full segment list
        private static int mountDepth(IReadOnlyList<string> segs) => 1;

        /// <summary>Resolve a file handle (its parent dir must resolve). Caller disposes the returned handle.</summary>
        private async Task<(FileSystemFileHandle? file, string? error)> ResolveFileAsync(IReadOnlyList<string> segs, bool create)
        {
            var parentSegs = segs.Take(segs.Count - 1).ToList();
            var name = VirtualFilePath.Name(segs);
            if (!VirtualFilePath.IsValidName(name)) return (null, $"Invalid file name '{name}'.");
            var (dir, owns, derr) = await ResolveDirectoryAsync(parentSegs, create);
            if (dir == null) return (null, derr);
            try
            {
                var file = await dir.GetFileHandle(name, create);
                return (file, null);
            }
            catch (Exception ex) { return (null, $"File '{VirtualFilePath.ToDisplay(segs)}' not found ({ex.GetType().Name})."); }
            finally { if (owns) dir.Dispose(); }
        }

        private async Task<bool> EnsureWritableAsync(FsMount mount)
        {
            // No prompt here (tool calls have no user gesture); reflect the current granted state.
            var perm = await mount.Handle.GetReadWritePermissions();
            mount.Permission = perm;
            return perm == "rw";
        }

        // ---- Persistence (IndexedDB) -----------------------------------------------------------------

        private async Task<IDBDatabase> GetDbAsync() => await IDBDatabase.OpenAsync(DB_NAME, 1, Db_OnUpgradeNeeded);

        private void Db_OnUpgradeNeeded(IDBVersionChangeEvent evt)
        {
            try
            {
                using var request = evt.Target;
                using var db = request.Result;
                var stores = db.ObjectStoreNames;
                if (!stores.Contains(HANDLES_STORE)) db.CreateObjectStore<string, FileSystemHandle>(HANDLES_STORE);
                if (!stores.Contains(META_STORE)) db.CreateObjectStore<string, string>(META_STORE);
            }
            catch (Exception ex) { Console.WriteLine($"[FS] IDB upgrade failed: {ex.Message}"); }
        }

        // IndexedDB pitfall: a transaction auto-commits ("finishes") once its pending requests resolve and
        // control returns to the event loop. So you must call tx.ObjectStore(...) SYNCHRONOUSLY right after
        // creating the transaction and never touch that transaction after an await. One store + one awaited
        // request per transaction is the safe unit (matches ShaderDebugService).
        private async Task SavePersistedAsync(FsMount mount)
        {
            using var db = await GetDbAsync();
            {
                using var tx = db.Transaction(META_STORE, true);
                using var meta = tx.ObjectStore<string, string>(META_STORE);
                await meta.PutAsync(mount.Kind, mount.Name);
            }
            if (mount.Kind != "opfs")
            {
                using var tx = db.Transaction(HANDLES_STORE, true);
                using var handles = tx.ObjectStore<string, FileSystemHandle>(HANDLES_STORE);
                await handles.PutAsync(mount.Handle, mount.Name);
            }
        }

        private async Task DeletePersistedAsync(string name)
        {
            using var db = await GetDbAsync();
            {
                using var tx = db.Transaction(META_STORE, true);
                using var meta = tx.ObjectStore<string, string>(META_STORE);
                await meta.DeleteAsync(name);
            }
            {
                using var tx = db.Transaction(HANDLES_STORE, true);
                using var handles = tx.ObjectStore<string, FileSystemHandle>(HANDLES_STORE);
                await handles.DeleteAsync(name);
            }
        }

        private async Task RestorePersistedMountsAsync()
        {
            using var db = await GetDbAsync();
            List<string> names;
            using (var tx = db.Transaction(META_STORE, false))
            {
                using var store = tx.ObjectStore<string, string>(META_STORE);
                using var keys = await store.GetAllKeysAsync();
                names = keys.ToList();
            }
            foreach (var name in names)
            {
                try
                {
                    string kind;
                    using (var tx = db.Transaction(META_STORE, false))
                    {
                        using var store = tx.ObjectStore<string, string>(META_STORE);
                        kind = await store.GetAsync(name);
                    }
                    FileSystemDirectoryHandle? handle;
                    if (kind == "opfs")
                    {
                        handle = await GetOpfsRootAsync();
                    }
                    else
                    {
                        using var tx = db.Transaction(HANDLES_STORE, false);
                        using var store = tx.ObjectStore<string, FileSystemHandle>(HANDLES_STORE);
                        var fsHandle = await store.GetAsync(name);
                        handle = fsHandle?.ToFileSystemDirectoryHandle(true);
                    }
                    if (handle == null) continue;
                    _mounts.Add(new FsMount
                    {
                        Name = name,
                        Kind = kind ?? "folder",
                        Handle = handle,
                        Persist = true,
                        Permission = await handle.GetReadWritePermissions(),
                    });
                }
                catch (Exception ex) { Console.WriteLine($"[FS] restore '{name}' failed: {ex.Message}"); }
            }
            if (_mounts.Count > 0) NotifyChanged();
        }

        // ---- Helpers ---------------------------------------------------------------------------------

        private async Task<FileSystemDirectoryHandle?> GetOpfsRootAsync()
        {
            try
            {
                using var storage = _js.Get<StorageManager>("navigator.storage");
                return await storage.GetDirectory();
            }
            catch { return null; }
        }

        private FsMount? FindMount(string name) => _mounts.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

        private string UniqueName(string baseName)
        {
            baseName = SanitizeName(baseName);
            if (FindMount(baseName) == null) return baseName;
            for (int i = 2; ; i++)
            {
                var candidate = $"{baseName}{i}";
                if (FindMount(candidate) == null) return candidate;
            }
        }

        private static string SanitizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "folder";
            // Only path separators would break virtual-path parsing (they split one mount name into
            // multiple segments). Everything else - spaces included - is preserved so the mount name
            // matches the real folder name.
            var cleaned = name.Trim().Replace('/', '_').Replace('\\', '_');
            return cleaned.Length == 0 ? "folder" : cleaned;
        }

        private void NotifyChanged() => OnChanged?.Invoke();

        private async Task NotifyGeminiMountAdded(FsMount mount)
        {
            try
            {
                await _gemini.NotifyToolContext(
                    $"The user mounted a folder in the virtual filesystem: /{mount.Name} ({mount.Kind}). " +
                    $"You can browse it with ListDirectory(\"/{mount.Name}\") and read/write files under it. " +
                    "A shared folder may be used to exchange messages (DevComms) with the user.");
            }
            catch (Exception ex) { Console.WriteLine($"[FS] notify add failed: {ex.Message}"); }
        }

        private async Task NotifyGeminiMountRemoved(string name)
        {
            try { await _gemini.NotifyToolContext($"The filesystem mount /{name} was removed and is no longer available."); }
            catch (Exception ex) { Console.WriteLine($"[FS] notify remove failed: {ex.Message}"); }
        }
    }
}
