using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;
using System.Text;
using System.Text.Json;

namespace Gemineachy.Services
{
    /// <summary>
    /// End-to-end test of EVERY <see cref="FileSystemService"/> tool, run on demand from the Files app.
    /// </summary>
    /// <remarks>
    /// Each step is a model message pushed through the real tool pipeline (<see cref="ChatAgentService.RunToolMessageAsync"/>:
    /// ParseCalls, the fenced/[Verbatim] bind, invocation) - built with the same code-region marks the page
    /// scraper produces - so it proves the path an agent's call actually takes, minus only the chat page.
    /// It works in a scratch folder under a silent OPFS mount and posts nothing to the conversation.
    /// Content is deliberately hostile: <c>List&lt;int&gt;</c>, quotes, backslashes, <c>$1</c>/<c>$&amp;</c>, CRLF,
    /// tabs, emoji, an empty file, regex metacharacters in a literal edit.
    /// Verdict: <c>document.documentElement</c> attribute <c>data-gem-fstest</c> ("PASS n/n" or "FAIL ..."),
    /// per-case detail in <c>data-gem-fstest-detail</c> (JSON) - readable via CDP.
    /// </remarks>
    public class FsSelfTest
    {
        private readonly SpawnJSRuntime _js;
        private readonly ChatAgentService _chat;
        private readonly FileSystemService _fs;
        public FsSelfTest(SpawnJSRuntime js, ChatAgentService chat, FileSystemService fs)
        {
            _js = js;
            _chat = chat;
            _fs = fs;
        }

        public record Case(string Name, bool Pass, string Detail);

        private const string T = "FileSystemService.";
        private readonly List<Case> _cases = new();
        private string _root = "";

        /// <summary>Run every case; returns the one-line verdict (also written to the page).</summary>
        public async Task<string> RunAsync()
        {
            _cases.Clear();
            var mount = await _fs.AddSilentOpfsMountAsync("fstest");
            if (mount == null) return Report("FAIL: OPFS is not available in this browser");
            _root = $"/{mount}/__gem_fstest__";
            try
            {
                await Raw("Delete", new() { ["path"] = _root, ["recursive"] = true }); // leftovers of an aborted run
                await RunCasesAsync();
            }
            catch (Exception ex) { Add("(harness)", false, $"threw {ex.GetType().Name}: {ex.Message}"); }
            finally
            {
                try { await Raw("Delete", new() { ["path"] = _root, ["recursive"] = true }); } catch { }
                _fs.RemoveSilentMount(mount);
            }
            var failed = _cases.Where(c => !c.Pass).ToList();
            var verdict = failed.Count == 0
                ? $"PASS {_cases.Count}/{_cases.Count}"
                : $"FAIL {_cases.Count - failed.Count}/{_cases.Count}: " + string.Join("; ", failed.Select(c => $"{c.Name} -> {c.Detail}"));
            return Report(verdict);
        }

        private async Task RunCasesAsync()
        {
            var r = _root;
            // A file's contents exactly as an agent would send them - every character the markdown renderer
            // or a JS String.replace could mangle.
            const string exact = "line1\r\nList<int> x = new(); var s = \"q\\\"z\";\n\tTabbed \\ backslash $1 $& $$ \U0001F4A1 na\u00EFve\n";

            // ---- directories + writing -----------------------------------------------------------
            Expect("MakeDirectory nested", await Call("MakeDirectory", new() { ["path"] = $"{r}/a/b/c" }), s => s.StartsWith("Created"));
            Expect("WriteFile hostile content via PAYLOAD", await Call("WriteFile", new() { ["path"] = $"{r}/a/exact.cs" }, ("content", exact)), s => s.StartsWith("Wrote"));
            await ExpectRead("ReadFile returns it byte-exact", $"{r}/a/exact.cs", exact);
            Expect("GetInfo size = UTF-8 byte count", await Call("GetInfo", new() { ["path"] = $"{r}/a/exact.cs" }),
                s => s.Contains($" {Encoding.UTF8.GetByteCount(exact)} bytes"));

            Expect("WriteFile first line", await Call("WriteFile", new() { ["path"] = $"{r}/a/log.txt" }, ("content", "first\n")), s => s.StartsWith("Wrote"));
            Expect("WriteFile append=true", await Call("WriteFile", new() { ["path"] = $"{r}/a/log.txt", ["append"] = true }, ("content", "second\n")), s => s.Contains("appended"));
            await ExpectRead("append keeps the old text, adds the new", $"{r}/a/log.txt", "first\nsecond\n");

            await Call("WriteFile", new() { ["path"] = $"{r}/a/short.txt" }, ("content", "a much longer first version\n"));
            await Call("WriteFile", new() { ["path"] = $"{r}/a/short.txt" }, ("content", "short"));
            await ExpectRead("overwrite with shorter text truncates", $"{r}/a/short.txt", "short");

            Expect("WriteFile empty content", await Call("WriteFile", new() { ["path"] = $"{r}/a/empty.txt" }, ("content", "")), s => s.StartsWith("Wrote 0"));
            Expect("empty file is 0 bytes", await Call("GetInfo", new() { ["path"] = $"{r}/a/empty.txt" }), s => s.Contains(" 0 bytes"));

            // \$ is written exactly as sent (it is legitimate in shell/regex/LaTeX) but the agent is told about it.
            Expect("WriteFile reports \\$ without changing it", await Call("WriteFile", new() { ["path"] = $"{r}/a/dollar.sh" }, ("content", "echo \\$HOME costs \\$5\n")),
                s => s.StartsWith("Wrote") && s.Contains("contains \\$ 2 times"));
            await ExpectRead("\\$ text kept byte-exact", $"{r}/a/dollar.sh", "echo \\$HOME costs \\$5\n");
            Expect("no \\$ note on ordinary text", await Call("WriteFile", new() { ["path"] = $"{r}/a/plain.txt" }, ("content", "price $5\n")),
                s => s.StartsWith("Wrote") && !s.Contains("Note:"));

            Expect("unicode file name", await Call("WriteFile", new() { ["path"] = $"{r}/a/na\u00EFve file \u2713.txt" }, ("content", "\u00FC")), s => s.StartsWith("Wrote"));
            await ExpectRead("unicode file name reads back", $"{r}/a/na\u00EFve file \u2713.txt", "\u00FC");

            // ---- the [Verbatim] guard, through the real bind -----------------------------------------
            var refused = await Raw("WriteFile", new() { ["path"] = $"{r}/a/refused.cs", ["content"] = "List<int> x;" }, fenced: false);
            Add("verbatim content from an UNFENCED call is refused", !refused.Ok && (refused.Error ?? "").Contains("PAYLOAD"), refused.Error ?? Show(refused.Result));
            Expect("refused call wrote nothing", await Call("GetInfo", new() { ["path"] = $"{r}/a/refused.cs" }), s => s.Contains("does not exist"));
            Expect("verbatim content inline in a FENCED call is accepted", await Call("WriteFile", new() { ["path"] = $"{r}/a/inline.txt", ["content"] = "a<b>\"c\"\\d" }), s => s.StartsWith("Wrote"));
            await ExpectRead("fenced inline content is exact", $"{r}/a/inline.txt", "a<b>\"c\"\\d");

            // ---- reading edge cases --------------------------------------------------------------
            Expect("ReadFile refuses above maxBytes", await Call("ReadFile", new() { ["path"] = $"{r}/a/exact.cs", ["maxBytes"] = 3 }), s => s.Contains("larger than maxBytes"));
            Expect("ReadFile missing file", await Call("ReadFile", new() { ["path"] = $"{r}/a/nope.txt" }), s => s.Contains("not found"));
            Expect("path escaping the root is rejected", await Call("ReadFile", new() { ["path"] = $"/{r.Split('/')[1]}/../../etc/x" }), s => s.Contains("escapes above"));
            Expect("unknown mount is reported", await Call("ReadFile", new() { ["path"] = "/no_such_mount_x/a.txt" }), s => s.Contains("No mount named"));

            // ---- listing ----------------------------------------------------------------------------
            Expect("ListDirectory shows files and dirs", await Call("ListDirectory", new() { ["path"] = $"{r}/a" }),
                s => s.Contains("exact.cs") && s.Contains("log.txt") && s.Contains("b/"));
            Expect("Tree shows nested dirs", await Call("Tree", new() { ["path"] = r, ["maxDepth"] = 4 }), s => s.Contains("c/"));
            Expect("GetInfo on a directory", await Call("GetInfo", new() { ["path"] = $"{r}/a/b" }), s => s.Contains("is a directory"));

            // ---- EditFile: literal, unique-or-fail, replaceAll ----------------------------------------
            await Call("WriteFile", new() { ["path"] = $"{r}/a/edit.txt" }, ("content", "alpha a.b*(c)[d] beta\nalpha again\n"));
            Expect("EditFile regex metacharacters matched literally", await Call("EditFile", new() { ["path"] = $"{r}/a/edit.txt" },
                ("oldText", "a.b*(c)[d]"), ("newText", "$1 cost $& \\o/")), s => s.Contains("replaced 1"));
            await ExpectRead("EditFile newText inserted literally ($1, $&, backslash)", $"{r}/a/edit.txt", "alpha $1 cost $& \\o/ beta\nalpha again\n");
            Expect("EditFile refuses an ambiguous oldText", await Call("EditFile", new() { ["path"] = $"{r}/a/edit.txt" }, ("oldText", "alpha"), ("newText", "x")),
                s => s.Contains("occurs 2 times"));
            Expect("EditFile replaceAll", await Call("EditFile", new() { ["path"] = $"{r}/a/edit.txt", ["replaceAll"] = true }, ("oldText", "alpha"), ("newText", "omega")),
                s => s.Contains("replaced 2"));
            await ExpectRead("EditFile replaceAll result", $"{r}/a/edit.txt", "omega $1 cost $& \\o/ beta\nomega again\n");
            Expect("EditFile oldText not found", await Call("EditFile", new() { ["path"] = $"{r}/a/edit.txt" }, ("oldText", "zzz"), ("newText", "y")),
                s => s.Contains("not found"));

            // ---- search / find / replace ---------------------------------------------------------------
            await Call("WriteFile", new() { ["path"] = $"{r}/a/s1.md" }, ("content", "Needle here\nno\nneedle again\n"));
            await Call("WriteFile", new() { ["path"] = $"{r}/a/b/s2.md" }, ("content", "NEEDLE\n"));
            await Call("WriteFile", new() { ["path"] = $"{r}/a/b/s3.txt" }, ("content", "needle\n"));
            Expect("SearchContent glob + case-insensitive", await Call("SearchContent", new() { ["path"] = r, ["pattern"] = "needle", ["filePattern"] = "*.md", ["caseInsensitive"] = true }),
                s => s.StartsWith("2 file(s), 3 match(es)"));
            Expect("SearchContent regex with anchors and \\w", await Call("SearchContent", new() { ["path"] = r, ["pattern"] = "^need\\w+ again$" }),
                s => s.StartsWith("1 file(s), 1 match(es)") && s.Contains("s1.md"));
            Expect("FindFiles by glob", await Call("FindFiles", new() { ["path"] = r, ["namePattern"] = "*.md" }), s => s.StartsWith("2 file(s)"));
            Expect("ReplaceInFiles dry run reports", await Call("ReplaceInFiles", new() { ["path"] = r, ["pattern"] = "(n|N)eedle", ["replacement"] = "$1EEDLE!", ["filePattern"] = "*.md" }),
                s => s.StartsWith("DRY RUN") && s.Contains("1 file(s), 2 replacement(s)"));
            await ExpectRead("dry run changed nothing", $"{r}/a/s1.md", "Needle here\nno\nneedle again\n");
            Expect("ReplaceInFiles applies with $1", await Call("ReplaceInFiles", new() { ["path"] = r, ["pattern"] = "(n|N)eedle", ["replacement"] = "$1EEDLE!", ["filePattern"] = "*.md", ["dryRun"] = false }),
                s => s.StartsWith("changed 1 file(s), 2 replacement(s)"));
            await ExpectRead("ReplaceInFiles capture group result", $"{r}/a/s1.md", "NEEDLE! here\nno\nnEEDLE! again\n");
            await ExpectRead("ReplaceInFiles left the non-matching file alone", $"{r}/a/b/s2.md", "NEEDLE\n");

            // ---- move / delete -------------------------------------------------------------------------
            Expect("Move into new nested folders", await Call("Move", new() { ["fromPath"] = $"{r}/a/log.txt", ["toPath"] = $"{r}/moved/deep/log2.txt" }), s => s.StartsWith("Moved"));
            await ExpectRead("moved file content intact", $"{r}/moved/deep/log2.txt", "first\nsecond\n");
            Expect("Move removed the source", await Call("GetInfo", new() { ["path"] = $"{r}/a/log.txt" }), s => s.Contains("does not exist"));
            Expect("Move onto an existing file overwrites", await Call("Move", new() { ["fromPath"] = $"{r}/a/empty.txt", ["toPath"] = $"{r}/moved/deep/log2.txt" }), s => s.StartsWith("Moved"));
            await ExpectRead("overwritten by the empty file", $"{r}/moved/deep/log2.txt", "");
            Expect("Delete non-empty dir needs recursive", await Call("Delete", new() { ["path"] = $"{r}/moved" }), s => s.Contains("Could not delete"));
            Expect("Delete recursive", await Call("Delete", new() { ["path"] = $"{r}/moved", ["recursive"] = true }), s => s.StartsWith("Deleted"));
            Expect("deleted dir is gone", await Call("GetInfo", new() { ["path"] = $"{r}/moved" }), s => s.Contains("does not exist"));
        }

        // ---- message building + dispatch through the real pipeline -------------------------------------

        /// <summary>A code block the way the page scraper hands it to the protocol.</summary>
        private static string Code(string text) => "\n" + ToolProtocol.CodeStart + text + ToolProtocol.CodeEnd + "\n";

        private static string Message(string tool, Dictionary<string, object?> args, bool fenced, (string Name, string Value)[] payloads)
        {
            var json = JsonSerializer.Serialize(new Dictionary<string, object?> { ["tool"] = T + tool, ["args"] = args });
            var call = $"{ToolProtocol.CallOpen}\n{json}\n{ToolProtocol.CallClose}";
            var sb = new StringBuilder(fenced ? Code(call) : "\n" + call + "\n");
            foreach (var (name, value) in payloads)
                sb.Append($"{ToolProtocol.PayloadOpen}:{name}»").Append(Code(value)).Append($"{ToolProtocol.PayloadClose}:{name}»\n");
            return sb.ToString();
        }

        private async Task<ChatAgentService.ToolResult> Raw(string tool, Dictionary<string, object?> args, bool fenced = true, params (string, string)[] payloads)
        {
            var results = await _chat.RunToolMessageAsync(Message(tool, args, fenced, payloads));
            return results.Count == 1 ? results[0]
                : new ChatAgentService.ToolResult { Tool = tool, Ok = false, Error = $"expected 1 parsed call, got {results.Count}" };
        }

        /// <summary>A fenced call; returns the tool's text, or "ERROR: ..." when the pipeline refused it.</summary>
        private async Task<string> Call(string tool, Dictionary<string, object?> args, params (string, string)[] payloads)
        {
            var res = await Raw(tool, args, true, payloads);
            return res.Ok ? res.Result?.ToString() ?? "" : "ERROR: " + res.Error;
        }

        private async Task ExpectRead(string name, string path, string expected)
        {
            var got = await Call("ReadFile", new() { ["path"] = path });
            Add(name, got == expected, got == expected ? "" : $"got {Show(got)} want {Show(expected)}");
        }

        private void Expect(string name, string result, Func<string, bool> ok)
        {
            bool pass;
            try { pass = ok(result); } catch { pass = false; }
            Add(name, pass, pass ? "" : Show(result));
        }

        private void Add(string name, bool pass, string detail)
        {
            _cases.Add(new Case(name, pass, detail));
            Console.WriteLine($"[FSTEST] {(pass ? "PASS" : "FAIL")} {name}{(pass ? "" : " - " + detail)}");
        }

        private static string Show(object? o)
        {
            var s = o?.ToString() ?? "(null)";
            s = s.Replace("\r", "\\r").Replace("\n", "\\n");
            return "\"" + (s.Length > 160 ? s.Substring(0, 160) + "…" : s) + "\"";
        }

        private string Report(string verdict)
        {
            Console.WriteLine($"[FSTEST] {verdict}");
            try
            {
                using var document = _js.Get<Document>("document");
                using var docEl = document.DocumentElement!;
                docEl.SetAttribute("data-gem-fstest", verdict);
                docEl.SetAttribute("data-gem-fstest-detail", JsonSerializer.Serialize(_cases));
            }
            catch (Exception ex) { Console.WriteLine($"[FSTEST] could not write DOM marker: {ex.Message}"); }
            return verdict;
        }
    }
}
