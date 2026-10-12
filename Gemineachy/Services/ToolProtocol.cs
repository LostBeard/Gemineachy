using System.ComponentModel;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Gemineachy.Services
{
    /// <summary>
    /// Pure (DOM-free, Gemini-free) tool-calling protocol logic: manifest/schema generation,
    /// tool-call parsing, and C# -> JSON-schema type mapping.
    /// Kept separate from <see cref="ChatAgentService"/> so it can be unit tested in isolation.
    /// </summary>
    public static class ToolProtocol
    {
        // Delimiters Gemini emits around a tool call. Chosen to be visually distinct, unlikely to
        // collide with normal prose, and to survive markdown rendering as literal innerText.
        public const string CallOpen = "«TOOL_CALL"; // «TOOL_CALL
        public const string CallClose = "»";          // »

        // ASCII fallback delimiters. The manifest only ever advertises the guillemet form (above), but
        // Gemini sometimes substitutes ASCII angle brackets for the guillemets and emits
        // <<TOOL_CALL {...}>> instead (observed live 2026-08-18). Same tolerance-not-contract stance as
        // the flat argument bag in ParseCalls: refusing the call only burns a round-trip and the user's
        // time, and the model does not reliably correct itself when told. An ASCII close is only honoured
        // for an ASCII open, so a ">>" inside a guillemet call's JSON can never truncate it.
        public const string CallOpenAscii = "<<TOOL_CALL";
        public const string CallCloseAscii = ">>";

        // Sentinel that marks the user-side message carrying tool results back to Gemini.
        public const string ResultsMarker = "[TOOL_RESULTS]";
        public const string ResultsFileName = "tool-results.json";

        // Sentinel that marks the user-side message carrying the tool manifest to Gemini.
        public const string ManifestMarker = "[TOOL_MANIFEST]";
        public const string ManifestFileName = "tool-manifest.json";

        // Sentinel that marks extension-injected game/orchestration prompts (machine-to-machine, not
        // user conversation). Hidden like tool traffic, but still counts as a genuine new turn for the
        // tool-loop guard (unlike TOOL_RESULTS/TOOL_MANIFEST plumbing).
        public const string GameMarker = "[GAME]";

        // Sentinel the AGENT may include in its own reply to have that reply hidden from the user (e.g.
        // when the user asks it not to narrate). Best placed at the very start so nothing flashes visible.
        public const string HiddenResponseMarker = "«HIDDEN»";

        // Marks the minimal tool note we APPEND to the user's FIRST message (so Gemini learns tools exist
        // without a separate startup message that would kill the new-chat welcome screen). Everything from
        // this marker to the end of that message is cosmetically hidden from the user's own chat bubble.
        public const string FirstNoteMarker = "⟦gemineachy-tools⟧";

        // Two alternatives rather than one pattern with alternating delimiters, so an ASCII close only
        // ever terminates an ASCII open (and vice versa). .NET allows the same group name in both.
        private static readonly Regex CallRegex = new Regex(
            "(?:" + Regex.Escape(CallOpen) + @"\s*(?<body>.+?)" + Regex.Escape(CallClose) + ")"
            + "|(?:" + Regex.Escape(CallOpenAscii) + @"\s*(?<body>.+?)" + Regex.Escape(CallCloseAscii) + ")",
            RegexOptions.Singleline | RegexOptions.Compiled);

        /// <summary>True if the text contains the start of a tool call in either delimiter form. Used by
        /// the DOM hiding pass, which must hide an ASCII-delimited call just as it hides a guillemet one.</summary>
        public static bool ContainsCallOpen(string? text) =>
            text != null && (text.Contains(CallOpen, StringComparison.Ordinal)
                             || text.Contains(CallOpenAscii, StringComparison.Ordinal));

        // A PAYLOAD block carries a VERBATIM argument value (code, file contents, long text) so it never
        // has to survive JSON-string escaping. The markers are plain text that survives markdown; the
        // body between them is (by instruction) a normal fenced code block, which Gemini's renderer
        // preserves exactly - the same reliable channel it uses to show code to a user. Example:
        //   «PAYLOAD:content»
        //   ```csharp
        //   ...code...
        //   ```
        //   «/PAYLOAD:content»
        // The id (":content") names the block after the argument it fills. A block is matched to a call
        // by NAME (name-form): put the value in «PAYLOAD:argName»…«/PAYLOAD:argName» and OMIT that
        // argument from the JSON - it is bound from the payload. (We intentionally do NOT support putting
        // the marker inside the JSON as a reference: the marker ends with », which is the CallClose
        // delimiter, so a marker inside the JSON would truncate the call block.)
        public const string PayloadOpen = "«PAYLOAD";
        public const string PayloadClose = "«/PAYLOAD";
        private static readonly Regex PayloadBlockRegex = new Regex(
            @"«PAYLOAD(?::(?<id>[^»\r\n]*))?»(?<body>.*?)«/PAYLOAD(?::[^»\r\n]*)?»",
            RegexOptions.Singleline | RegexOptions.Compiled);

        // Code-region marks. The page scraper wraps the text of every rendered code block in these two
        // private-use characters, so the protocol knows exactly which parts of a reply came out of a code
        // block (verbatim) and which came out of rendered markdown prose (lossy: <tags> stripped, backslash
        // escapes and *emphasis* consumed - undetectably, since what was lost is simply absent). They never
        // reach a tool: every value is passed through StripCodeMarks first.
        public const char CodeStart = '';
        public const char CodeEnd = '';

        /// <summary>Placed inside a code region by the scraper when the page rendered that code block only in part
        /// (a virtualizing editor keeps just the lines near the viewport in the DOM). Its text is NOT the whole
        /// block, so a payload or call holding this mark is refused instead of run on truncated text.</summary>
        public const char CodeIncomplete = '';

        /// <summary>Remove the scraper's code-region marks (see <see cref="CodeStart"/>).</summary>
        public static string StripCodeMarks(string? text) =>
            string.IsNullOrEmpty(text) || text.IndexOfAny(new[] { CodeStart, CodeEnd, CodeIncomplete }) < 0
                ? text ?? ""
                : text.Replace(CodeStart.ToString(), "").Replace(CodeEnd.ToString(), "").Replace(CodeIncomplete.ToString(), "");

        /// <summary>Lines per part the model is asked for when a code block could not be read in full. The
        /// smallest editor that virtualizes (Qwen's Monaco) keeps about 38 lines rendered.</summary>
        public const int IncompleteChunkLines = 30;

        /// <summary>The error for a call whose code arrived only in part (see <see cref="CodeIncomplete"/>).</summary>
        public static string IncompleteCodeError(string what) =>
            $"{what} was in a code block too long for this chat page to show in full - the page only renders part of a "
            + "long code block, so it could not be read exactly, and nothing was run. Send the value in parts of at most "
            + $"{IncompleteChunkLines} lines, one tool call per part (for a file: WriteFile with the first part, then "
            + "WriteFile with append=true for each next part).";

        /// <summary>The marked code regions of a scraped message, as (index of CodeStart, index of CodeEnd).</summary>
        private static List<(int Start, int End)> CodeRegions(string text)
        {
            var regions = new List<(int, int)>();
            int start = -1;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == CodeStart) start = i;
                else if (text[i] == CodeEnd && start >= 0) { regions.Add((start, i)); start = -1; }
            }
            return regions;
        }

        /// <summary>True when every non-whitespace character of [from, to) (code marks aside) lies inside ONE
        /// code region - i.e. nothing in the span went through markdown prose rendering.</summary>
        /// <remarks>
        /// Covers every shape models produce: markers outside the fence (the advertised form), markers inside
        /// it, and the mixed one Gemini actually wrote 2026-10-12 - one outer fence around the call AND the
        /// payload's opening marker, an inner ```csharp line left literal, the inner closing ``` closing the
        /// OUTER block, and the payload's closing marker landing in prose. The code was still exact there.
        /// </remarks>
        private static bool IsFenced(string text, List<(int Start, int End)> regions, int from, int to)
        {
            int region = -1;
            bool sawCode = false;
            for (int i = from; i < to; i++)
            {
                var ch = text[i];
                if (ch == CodeStart) sawCode = true;
                if (ch == CodeStart || ch == CodeEnd || char.IsWhiteSpace(ch)) continue;
                int r = regions.FindIndex(g => i > g.Start && i < g.End);
                if (r < 0) return false;                  // prose
                if (region >= 0 && r != region) return false; // spans two code blocks
                region = r;
            }
            return region >= 0 || sawCode;   // an EMPTY code block is still a code block (an empty file)
        }

        private static readonly JsonSerializerOptions ArgOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() },
        };

        private static readonly JsonSerializerOptions ResultOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() },
            WriteIndented = false,
        };

        public class ParsedCall
        {
            public string Tool { get; set; } = "";
            /// <summary>Named argument object. Undefined if the model emitted malformed JSON.</summary>
            public JsonElement Args { get; set; }
            public bool HasArgs { get; set; }
            /// <summary>Non-null when the block could not be parsed as a valid call.</summary>
            public string? ParseError { get; set; }
            /// <summary>The raw text between the delimiters (for logging/diagnostics).</summary>
            public string Raw { get; set; } = "";
            /// <summary>PAYLOAD blocks found in the SAME model message (id -> verbatim body), shared by
            /// every call in that message. Used both for reference-form (an arg value equal to a payload
            /// marker) and name-form (a payload whose id matches a still-unbound parameter name).</summary>
            public IReadOnlyDictionary<string, string>? Payloads { get; set; }
            /// <summary>True when the call's JSON came out of a code block, so its text is exactly what the
            /// model wrote. False = it was scraped from rendered markdown prose and may have lost characters.</summary>
            public bool Fenced { get; set; }
            /// <summary>Ids of this message's PAYLOAD blocks whose value did NOT come out of a single code
            /// block (see <see cref="Fenced"/>). A [Verbatim] parameter refuses such a value.</summary>
            public IReadOnlySet<string>? UnfencedPayloads { get; set; }
        }

        /// <summary>Extract every PAYLOAD block from a model message as id -> verbatim body. The body is
        /// used exactly as written (no unescaping), with the wrapping code fence and the single edge
        /// newlines removed. Message-level (payloads are shared across all tool calls in the message).</summary>
        public static Dictionary<string, string> ExtractPayloads(string? text) => ExtractPayloads(text, out _);

        /// <summary><see cref="ExtractPayloads(string?)"/>, also reporting which payload ids were NOT a single
        /// code block - i.e. whose value was scraped from rendered prose and cannot be trusted verbatim.</summary>
        public static Dictionary<string, string> ExtractPayloads(string? text, out HashSet<string> unfenced)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            unfenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(text)) return map;
            var regions = CodeRegions(text);
            foreach (Match m in PayloadBlockRegex.Matches(text))
            {
                var id = (m.Groups["id"].Success ? m.Groups["id"].Value : "").Trim();
                var body = m.Groups["body"];
                // Fenced when the whole block sits inside a code block (markers included), or the body is
                // exactly one code block (markers outside the fence - the advertised form).
                var fenced = IsFenced(text, regions, body.Index, body.Index + body.Length);
                map[id] = NormalizePayloadBody(StripCodeMarks(body.Value)); // last block wins on duplicate id
                if (fenced) unfenced.Remove(id); else unfenced.Add(id);
            }
            return map;
        }

        /// <summary>True when [from, to) overlaps a code region the page rendered only in part (one holding
        /// <see cref="CodeIncomplete"/>). The mark sits at the region's start, which a call or payload inside
        /// that block need not include, so overlap with the REGION is what counts.</summary>
        private static bool TouchesIncomplete(string text, List<(int Start, int End)> regions, int from, int to) =>
            regions.Any(g => g.Start < to && g.End >= from && text.IndexOf(CodeIncomplete, g.Start, g.End - g.Start) >= 0);

        /// <summary>The id of the first PAYLOAD block that touches an incomplete code region, or null.</summary>
        private static string? IncompletePayloadId(string text)
        {
            if (text.IndexOf(CodeIncomplete) < 0) return null;
            var regions = CodeRegions(text);
            foreach (Match m in PayloadBlockRegex.Matches(text))
                if (TouchesIncomplete(text, regions, m.Index, m.Index + m.Length))
                    return (m.Groups["id"].Success ? m.Groups["id"].Value : "").Trim();
            return null;
        }

        /// <summary>Strip the single leading/trailing line break the markers sit on, then - if the body is
        /// still wrapped in a ``` code fence (i.e. the renderer left the fence backticks in the text
        /// instead of consuming them) - remove that wrapping fence too. What remains is the exact value.</summary>
        private static string NormalizePayloadBody(string body)
        {
            body = Regex.Replace(body, @"\A\r?\n", "");
            // \z (absolute end), NOT $ : $ also matches BEFORE a final newline, so on a body ending
            // "}\n\n" both newlines matched and Replace stripped both - eating the file's own trailing
            // newline along with the one the closing marker sits on. \z strips exactly the marker's.
            body = Regex.Replace(body, @"\r?\n[ \t]*\z", "");
            var fence = Regex.Match(body, @"^```[^\n]*\r?\n(?<code>.*?)\r?\n```[ \t]*$", RegexOptions.Singleline);
            if (fence.Success) return fence.Groups["code"].Value;
            // An opening fence line with NO closing fence: the renderer used the payload's closing ``` to close
            // an OUTER fence the model had wrapped around the whole call (Gemini, 2026-10-12), so the inner
            // opening line ("```csharp") survived as literal text inside that outer block. It is syntax, not
            // file content - drop it. (A value with a real leading fence line arrives balanced, matched above.)
            var open = Regex.Match(body, @"\A```[\w#+.\-]*[ \t]*\r?\n");
            if (open.Success && !Regex.IsMatch(body, @"(^|\n)```[ \t]*\r?\n?\z")) return body.Substring(open.Length);
            return body;
        }

        /// <summary>
        /// Extract every tool-call block from a model response. Never throws - malformed blocks are
        /// returned with <see cref="ParsedCall.ParseError"/> set so the caller can report them back
        /// to the model instead of silently dropping them.
        /// </summary>
        /// <remarks>
        /// Argument shape is tolerant by design. The manifest documents the nested form
        /// (<c>{"tool":"X","args":{...}}</c>), but LLMs overwhelmingly prefer to emit the arguments
        /// FLAT (<c>{"tool":"X","move":"11-15"}</c>) and will do so repeatedly even when told not to
        /// (observed: Gemini emitting the flat form 4x in a row before conforming). Rather than fight
        /// that with prompt nagging and eat a run of "missing argument" failures at the start of every
        /// game, we accept both: if an <c>args</c> object is present it wins; otherwise every top-level
        /// key except the reserved <c>tool</c>/<c>args</c> is taken as a named argument. Nested is never
        /// worse, so this is pure tolerance, not a lowered bar.
        /// </remarks>
        public static List<ParsedCall> ParseCalls(string? responseText)
        {
            var calls = new List<ParsedCall>();
            if (string.IsNullOrEmpty(responseText)) return calls;
            responseText = RecoverLabelledCalls(responseText);
            // Payloads are message-level: extract once, share with every call in the message.
            var payloads = ExtractPayloads(responseText, out var unfencedPayloads);
            var incompletePayload = IncompletePayloadId(responseText);
            var regions = CodeRegions(responseText);
            foreach (Match m in CallRegex.Matches(responseText))
            {
                var body = m.Groups["body"];
                var fenced = IsFenced(responseText, regions, body.Index, body.Index + body.Length);
                var raw = StripCodeMarks(body.Value).Trim();
                var call = new ParsedCall { Raw = raw, Payloads = payloads, Fenced = fenced, UnfencedPayloads = unfencedPayloads };
                // Truncated text must never run. A payload is message-level (any call may bind it), so one
                // incomplete payload refuses every call in the message.
                if (TouchesIncomplete(responseText, regions, body.Index, body.Index + body.Length)) { call.ParseError = IncompleteCodeError("This tool call"); calls.Add(call); continue; }
                if (incompletePayload != null) { call.ParseError = IncompleteCodeError($"{PayloadOpen}:{incompletePayload}»"); calls.Add(call); continue; }
                try
                {
                    using var doc = JsonDocument.Parse(raw);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                    {
                        call.ParseError = "Tool call JSON must be an object like {\"tool\":\"Name\",\"args\":{...}}.";
                    }
                    else if (TryGetPropertyIgnoreCase(root, "tool") is not JsonElement toolEl
                             || toolEl.ValueKind != JsonValueKind.String
                             || string.IsNullOrWhiteSpace(toolEl.GetString()))
                    {
                        call.ParseError = "Tool call JSON is missing a non-empty \"tool\" property.";
                    }
                    else
                    {
                        call.Tool = toolEl.GetString()!;
                        // Build the argument bag: an explicit "args" object wins; otherwise the flat
                        // top-level keys (minus tool/args) are the arguments.
                        var bag = new Dictionary<string, JsonElement>();
                        if (TryGetPropertyIgnoreCase(root, "args") is JsonElement argsEl
                            && argsEl.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var prop in argsEl.EnumerateObject()) bag[prop.Name] = prop.Value.Clone();
                        }
                        else
                        {
                            foreach (var prop in root.EnumerateObject())
                            {
                                if (string.Equals(prop.Name, "tool", StringComparison.OrdinalIgnoreCase)) continue;
                                if (string.Equals(prop.Name, "args", StringComparison.OrdinalIgnoreCase)) continue;
                                bag[prop.Name] = prop.Value.Clone(); // last-writer-wins on dup keys, like JSON
                            }
                        }
                        // Name-form payloads (a «PAYLOAD:argName» block filling an argument omitted from the
                        // JSON) are applied later, in binding, where the parameter names are known.
                        call.Args = JsonSerializer.SerializeToElement(bag, ArgOptions);
                        // An args object (possibly empty) is always available now.
                        call.HasArgs = call.Args.ValueKind == JsonValueKind.Object;
                    }
                }
                catch (JsonException ex)
                {
                    call.ParseError = $"Invalid tool-call JSON: {ex.Message}. If an argument value contains code, quotes, "
                        + $"backslashes, or newlines, do NOT place it in the JSON - send it verbatim as a {PayloadOpen}:argName» … "
                        + $"{PayloadClose}:argName» code block instead (see the tool manifest)."
                        + (fenced ? "" : " Also emit the call itself inside a ``` code block: this one was outside one, and the "
                            + "page's markdown rendering strips backslashes and <...> from text outside code blocks, "
                            + "which is likely what broke the JSON.");
                }
                calls.Add(call);
            }
            return calls;
        }

        /// <summary>
        /// Order-sensitive signature of a round's calls, for the loop guard that refuses to re-run a call identical
        /// to one that just failed: tool name + argument JSON + the message's PAYLOAD blocks (value, and whether it
        /// came from a code block). The payloads count because they ARE arguments: Z.ai's GLM fixed a refused
        /// payload's layout (2026-10-12) and resent the same call JSON, and a JSON-only signature called the
        /// corrected call "identical" and never ran it.
        /// </summary>
        public static string RoundSignature(IEnumerable<ParsedCall> calls)
        {
            var list = calls.ToList();
            var calls0 = string.Join("␞", list.Select(c => c.ParseError != null
                ? $"!{c.Raw}"
                : $"{c.Tool}({(c.HasArgs ? c.Args.GetRawText() : "")})"));
            var first = list.FirstOrDefault();   // payloads are message-level: every call shares the same set
            if (first?.Payloads == null || first.Payloads.Count == 0) return calls0;
            var payloads = string.Join("␞", first.Payloads.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select(p => $"{p.Key}{(first.UnfencedPayloads?.Contains(p.Key) == true ? "~" : "=")}{p.Value}"));
            return calls0 + "␝" + payloads;
        }

        private static readonly Regex LabelledCallBody = new Regex(
            @"\A\s*\{.*""tool"".*\}\s*" + Regex.Escape(CallClose) + @"\s*\z", RegexOptions.Singleline | RegexOptions.Compiled);

        /// <summary>
        /// Restore the opening marker of a call whose model wrote it as the code fence's LANGUAGE
        /// (<c>```«TOOL_CALL</c>): the page renders the info string as the block's label, which the scraper drops
        /// as chrome, so the block reads <c>{"tool":…}»</c> with no opening marker. A code block holding exactly a
        /// JSON object with a "tool" key followed by the closing marker is unambiguous. Measured: Z.ai's GLM,
        /// 2026-10-12 (1 of 4 fresh chats).
        /// </summary>
        private static string RecoverLabelledCalls(string text)
        {
            if (text.IndexOf(CodeStart) < 0) return text;
            var regions = CodeRegions(text);
            for (int r = regions.Count - 1; r >= 0; r--)
            {
                var (start, end) = regions[r];
                var body = text.Substring(start + 1, end - start - 1);
                if (body.Contains(CallOpen, StringComparison.Ordinal) || !LabelledCallBody.IsMatch(body)) continue;
                text = text.Insert(start + 1, CallOpen + "\n");
            }
            return text;
        }

        /// <summary>Case-insensitive top-level property lookup (JsonElement.TryGetProperty is ordinal).</summary>
        private static JsonElement? TryGetPropertyIgnoreCase(JsonElement obj, string name)
        {
            foreach (var prop in obj.EnumerateObject())
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                    return prop.Value;
            return null;
        }

        // Built-in discovery tool names (registered by ChatAgentService). Referenced in the manifest
        // text so the model knows how to fetch a tool's full argument schema on demand.
        public const string SearchToolsName = "ChatAgentService.SearchTools";
        public const string GetToolSchemaName = "ChatAgentService.GetToolSchema";
        public const string ListToolsName = "ChatAgentService.ListTools";

        /// <summary>
        /// Build the standing manifest: protocol instructions (call/result/hidden formats) + how to
        /// discover a tool's arguments on demand + a COMPACT INDEX of the current tools (name + one-line
        /// summary only, no argument schemas). The bulky per-tool argument schemas are fetched on demand
        /// via <see cref="SearchToolsName"/>/<see cref="GetToolSchemaName"/> so this scales to a large
        /// tool set without a giant always-present payload.
        /// </summary>
        public static string BuildManifest(IEnumerable<ToolCall> tools)
        {
            var sb = new StringBuilder();
            // The user's voice, like the first-message note: it is the user's intent, and a message that disclaims
            // the user reads as injection to some models (see BuildFirstMessageToolNote).
            sb.AppendLine("I'm using the Gemineachy browser extension. Its tools are not built-in tools you call - you WRITE a tool call as a code block in your reply, in the format below, and the extension running in my browser reads it, executes it, and sends you the result. This really works, so when I ask for something a tool can do, write the tool call instead of saying you can't. Please read this carefully.");
            sb.AppendLine();
            AppendProtocolSection(sb);
            sb.AppendLine("## Discovering a tool's arguments");
            sb.AppendLine("The tools below are listed by NAME and a one-line summary only - not their arguments. Before calling a tool whose arguments you are unsure of, look them up:");
            sb.AppendLine($"- {SearchToolsName}: pass keywords (space/comma separated); returns the matching tools WITH their full argument schemas.");
            sb.AppendLine($"- {GetToolSchemaName}: pass one tool name (or a comma-separated list); returns their full argument schemas.");
            sb.AppendLine($"- {ListToolsName}: returns this index again (names + summaries) if you lose track.");
            sb.AppendLine("Simple tools whose arguments are obvious from the summary can be called directly; look up the schema whenever unsure.");
            sb.AppendLine();
            sb.AppendLine("## Available tools");
            sb.AppendLine(IndexLegend);
            sb.AppendLine(BuildToolIndex(tools));
            return sb.ToString();
        }

        /// <summary>Shared protocol section (call format, results, hidden-reply) used by the full
        /// manifest and referenced conceptually by change notices.</summary>
        private static void AppendProtocolSection(StringBuilder sb)
        {
            sb.AppendLine("## Calling a tool");
            sb.AppendLine("When you want to call a tool, emit a fenced code block containing the two marker lines and a single JSON object, EXACTLY like this. Use a real ``` code block (the same formatting you use to show code to a user) - that channel preserves the text exactly, which is what makes the call parse reliably:");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine(CallOpen);
            sb.AppendLine("{\"tool\":\"<tool name>\",\"args\":{ ...named arguments... }}");
            sb.AppendLine(CallClose);
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("- Use the exact tool name from the index.");
            sb.AppendLine("- \"args\" is an object of NAMED arguments matching the tool's parameters. Omit optional arguments to accept their defaults; use {} when the tool takes no arguments.");
            sb.AppendLine("- Keep the JSON SMALL and simple. For any argument value that is code, a file's contents, or long / multi-line text, do NOT inline it in the JSON (escaping quotes and backslashes there is error-prone) - send it as a PAYLOAD block (see below).");
            sb.AppendLine("- You may emit multiple tool-call blocks in one reply; each block is one call and they run in order.");
            sb.AppendLine($"- After emitting tool calls, stop and wait. The extension runs them and replies with a {ResultsMarker} user message containing the results, then you continue.");
            sb.AppendLine();
            sb.AppendLine("## Sending code, file contents, or long/complex text (PAYLOAD blocks)");
            sb.AppendLine("A JSON string is a poor container for code: escaping every quote, backslash and newline is error-prone and easily corrupted. Instead send such a value as a PAYLOAD block - an ordinary fenced code block (exactly how you would show code to a user) bracketed by plain-text markers. The text between the markers is used VERBATIM as the argument; you escape NOTHING - write it exactly as the file should read (no \\\" for quotes, no \\$ for dollar signs).");
            sb.AppendLine();
            sb.AppendLine("Preferred form - name the PAYLOAD after the argument, and omit that argument from the JSON:");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine(CallOpen);
            sb.AppendLine("{\"tool\":\"FileSystemService.WriteFile\",\"args\":{\"path\":\"/notes/Program.cs\"}}");
            sb.AppendLine(CallClose);
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine($"{PayloadOpen}:content»");
            sb.AppendLine("```csharp");
            sb.AppendLine("using System;");
            sb.AppendLine("class P { static void Main() => Console.WriteLine(\"Hello, World!\"); }");
            sb.AppendLine("```");
            sb.AppendLine($"{PayloadClose}:content»");
            sb.AppendLine();
            sb.AppendLine($"The block named \"content\" becomes the `content` argument, exactly as written between the markers - note the quotes need no escaping. Put the marker lines OUTSIDE the code fence and the value INSIDE it, and do NOT also put \"content\" in the JSON.");
            sb.AppendLine("- The payload name must match the argument name exactly.");
            sb.AppendLine("- When a call needs more than one such argument, add one named PAYLOAD block per argument (each matching its argument's name).");
            sb.AppendLine("- Do NOT put the marker text inside the JSON; the JSON holds only the small/simple arguments, and each PAYLOAD block follows the tool-call block.");
            sb.AppendLine("- Arguments marked * in the tool index are VERBATIM: they are only accepted from a PAYLOAD block (or a call inside a code block). Text outside code blocks goes through markdown rendering, which silently drops <...>, backslashes and *emphasis*.");
            sb.AppendLine("- If the value itself contains ``` lines (a Markdown file, for example), open and close its code block with four backticks (````) so the inner fences stay part of the value.");
            sb.AppendLine();
            sb.AppendLine("## Results");
            sb.AppendLine($"Results arrive as a {ResultsMarker} user message with the results JSON inlined in the message body: an array, in call order, of objects shaped {{\"tool\":string,\"ok\":bool,\"result\":any}} on success or {{\"tool\":string,\"ok\":false,\"error\":string}} on failure.");
            sb.AppendLine();
            sb.AppendLine("## Hiding your own reply");
            sb.AppendLine($"You can suppress a reply from the user's view by starting it with {HiddenResponseMarker} (put it first so nothing flashes on screen). Use this when the user asks you not to narrate, or when a reply is purely mechanical. The user can still reveal hidden messages with the extension's \"Show tool calls\" toggle. Default to replying normally (visible) unless the user has asked otherwise.");
            sb.AppendLine();
        }

        /// <summary>
        /// The minimal note appended to the user's FIRST message so Gemini knows tools EXIST and how to
        /// discover them - without dumping the whole manifest and without a separate startup message (which
        /// would kill the new-chat welcome screen). Kept short on purpose: it points at the discovery tools
        /// rather than listing every tool. The <see cref="FirstNoteMarker"/> lets the extension hide this
        /// note from the user's own chat bubble while Gemini still receives it.
        /// </summary>
        /// <remarks>
        /// Written in the USER's voice, because it is the user's intent - they installed the extension to give the
        /// model tools. An earlier "[Automated note ... NOT from the user]" framing worked on Gemini, but Duck.ai's
        /// GPT model treated it as third-party injection and answered "I can't access a file-writing tool in this
        /// chat" (2026-10-12): models are trained to distrust instructions that disclaim the user. Even in the
        /// user's voice it still declined about half the time, reasoning from its BUILT-IN tool list. So the note
        /// asks it to BEGIN with a ListTools call - writing text, which it does not refuse - and the real results
        /// that come back are the evidence that the tools exist before it decides what it can do.
        /// </remarks>
        public static string BuildFirstMessageToolNote() =>
            FirstNoteMarker + "\n"
            + "(I'm using the Gemineachy browser extension. These are not built-in tools you call - you WRITE a tool call "
            + "as a code block in your reply, in the format below, and the extension running in my browser reads it, "
            + "executes it, and sends you the result. This really works: for example it can read and write files in a "
            + "virtual filesystem. So when I ask for something a tool can do, write the tool call instead of saying you "
            + $"can't. Please BEGIN your reply to this message by writing a call to {ListToolsName}: you will get the "
            + "real list of tools back, and then you can continue with my request using them. No need to mention this note.)\n"
            + BuildCallRules();

        /// <summary>
        /// The hidden warm-up sent before the first message of a chat on sites whose model will not believe it has
        /// tools from a note alone (see IChatSite.IntroduceWithWarmUp). Its ONLY ask is to write one ListTools
        /// request; the ListTools answer that comes back carries the index AND <see cref="BuildCallRules"/>, so the
        /// model learns the format from a real working exchange. Starts with <see cref="ManifestMarker"/> (hidden).
        /// </summary>
        /// <remarks>
        /// Wording A/B-measured on Duck.ai's default model, fresh chats, 2026-10-12 - every phrase here matters:
        /// <list type="bullet">
        /// <item>"tools you call / executes it" + "reply «HIDDEN» ready": followed ~1 in 3; refusals named
        /// "execute tools" and "agree to conceal the result" (asking the model to hide its reply reads as deception -
        /// so the extension hides the "ready" reply itself instead).</item>
        /// <item>"I installed it, I trust it ... work with files on my computer": 0/5 ("access your computer").</item>
        /// <item>THIS neutral helper framing: 5/5 (a format-only framing without the hide request: 8/9).</item>
        /// </list>
        /// </remarks>
        public static string BuildWarmUpMessage() =>
            $"{ManifestMarker} Helper requests\n\n"
            + "This chat is connected to a helper (a browser extension I use) that answers structured requests. To send it a "
            + "request, write a request block in the format below; its answer arrives as my next message. I'd like you to "
            + "use it whenever a request block would help with what I ask.\n\n"
            + "Please send it a first request now - reply with ONLY this block and nothing else:\n"
            + $"```\n{CallOpen}\n{{\"tool\":\"{ListToolsName}\",\"args\":{{}}}}\n{CallClose}\n```\n"
            + "When its answer arrives, just reply: ready";

        /// <summary>
        /// The one-line hint that rides the user's first message after a FOLLOWED warm-up - same neutral "helper"
        /// language as the warm-up. Measured: with the warm-up alone, Duck.ai's model still answered one real
        /// request in four with "I'm unable to access the helper's file tools". Marked like the first-message note,
        /// so the extension hides it from the user's bubble.
        /// </summary>
        public static string BuildPostWarmUpHint() =>
            FirstNoteMarker + "\n(If the helper can do this, send it a request block - its answer will come back to you.)";

        /// <summary>Compact index: one line per tool, "- name(args) : one-line summary". The argument NAMES
        /// are listed (so the model never has to guess one - it once wrote WriteFile's content as "text"),
        /// but not their types or descriptions; those come from the schema tools.</summary>
        public static string BuildToolIndex(IEnumerable<ToolCall> tools)
        {
            var sb = new StringBuilder();
            foreach (var t in tools)
                sb.AppendLine($"- {t.ToolName}({ArgumentList(t)}) : {OneLine(t.Description)}");
            var s = sb.ToString().TrimEnd();
            return s.Length == 0 ? "(none)" : s;
        }

        /// <summary>"path, content*, append?" - * = verbatim (send as a PAYLOAD), ? = optional.</summary>
        public static string ArgumentList(ToolCall tool) =>
            string.Join(", ", tool.MethodInfo.GetParameters().Select(p =>
                (p.Name ?? "arg") + (IsVerbatim(p) ? "*" : "") + (p.HasDefaultValue ? "?" : "")));

        /// <summary>How to read the index's argument lists. Shown wherever the index is.</summary>
        public const string IndexLegend =
            "Each tool is listed as name(arguments): arg? = optional; arg* = VERBATIM (code, file contents, a regex or other exact text) - send it as a PAYLOAD code block, never inside the JSON.";

        /// <summary>
        /// The calling rules in their shortest complete form. Carried by every message that can be the model's
        /// FIRST sight of the tools - the first-message note, ListTools, the schema lookups - because the model
        /// only follows a protocol it has actually been shown. (The 2026-10-12 C# write corruption: the model
        /// reached WriteFile through the first-message note + ListTools, never saw the PAYLOAD rule, and wrote
        /// the code into an unfenced JSON string, where markdown rendering ate <int> and every \".)
        /// </summary>
        public static string BuildCallRules() =>
            "How to call a tool: write a ``` code block containing exactly\n"
            + $"{CallOpen}\n{{\"tool\":\"Name\",\"args\":{{\"argName\":value}}}}\n{CallClose}\n"
            + $"then stop; the results come back in a {ResultsMarker} message. "
            + "Text outside code blocks is rendered as markdown, which silently drops <...>, backslashes and *emphasis* - so the call must be inside the code block.\n"
            + "An argument marked * must NOT go in the JSON: after the call, write\n"
            + $"{PayloadOpen}:argName»\n"
            + "then a ``` code block holding the exact value - written exactly as the file should read, escaping NOTHING (not quotes, not backslashes, not $) - then\n"
            + $"{PayloadClose}:argName»\n"
            + "each marker on its own line OUTSIDE the code block. The call's code block and the value's code block are SEPARATE - "
            + "never put one inside the other (a ``` line inside a code block ends it). If the value itself contains ``` lines (a Markdown file, for example), open and close its code block with four backticks (````).";

        /// <summary>First sentence (or a trimmed prefix) of a description, on a single line.</summary>
        private static string OneLine(string? s, int max = 160)
        {
            s = (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (s.Length == 0) return "(no description)";
            var dot = s.IndexOf(". ", StringComparison.Ordinal);
            if (dot > 0 && dot + 1 <= max) return s.Substring(0, dot + 1);
            return s.Length <= max ? s : s.Substring(0, max).TrimEnd() + "…";
        }

        /// <summary>Keyword search over tool name + description. Terms are OR-matched and results ranked
        /// by how many distinct terms hit; an empty query returns all tools.</summary>
        public static List<ToolCall> MatchTools(IEnumerable<ToolCall> tools, string? query)
        {
            var terms = (query ?? "")
                .Split(new[] { ' ', ',', ';', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.ToLowerInvariant()).Distinct().ToArray();
            var list = tools.ToList();
            if (terms.Length == 0) return list;
            return list
                .Select(t => (tool: t, score: terms.Count(term => ($"{t.ToolName} {t.Description}").ToLowerInvariant().Contains(term))))
                .Where(x => x.score > 0)
                .OrderByDescending(x => x.score)
                .Select(x => x.tool)
                .ToList();
        }

        /// <summary>Build the delta message sent when tools register/unregister: what was added (with
        /// one-line summaries) and removed (names), plus a brief reminder of how to call/discover. Only
        /// the change is sent - never the full schema set.</summary>
        public static string BuildToolChangeMessage(IReadOnlyList<ToolCall> added, IReadOnlyList<string> removed, string? addendum = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Available tools changed.");
            if (added.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Now available (added):");
                sb.AppendLine(IndexLegend);
                sb.AppendLine(BuildToolIndex(added));
            }
            if (removed.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("No longer available (removed): " + string.Join(", ", removed));
            }
            sb.AppendLine();
            sb.AppendLine($"Call format is unchanged: {CallOpen} {{\"tool\":\"Name\",\"args\":{{...}}}} {CallClose}. For an added tool's arguments, call {SearchToolsName} (keywords) or {GetToolSchemaName} (name).");
            if (!string.IsNullOrWhiteSpace(addendum))
            {
                sb.AppendLine();
                sb.AppendLine(addendum);
            }
            return sb.ToString();
        }

        /// <summary>Serialize the tool set as a JSON array of function-calling schemas.</summary>
        public static string SerializeSchemas(IEnumerable<ToolCall> tools)
        {
            var schemas = tools.Select(BuildToolSchema).ToList();
            return JsonSerializer.Serialize(schemas, ResultOptions);
        }

        /// <summary>Build a single standard function-calling schema for a tool from its delegate signature.</summary>
        public static Dictionary<string, object?> BuildToolSchema(ToolCall tool)
        {
            var method = tool.MethodInfo;
            var properties = new Dictionary<string, object?>();
            var required = new List<string>();
            foreach (var p in method.GetParameters())
            {
                var name = p.Name ?? "arg";
                properties[name] = BuildParamSchema(p);
                // Required iff it has no default. This matches TryBindArguments, which fails any
                // omitted parameter that has no default (nullable or not). To make a param optional,
                // give it a default value.
                if (!p.HasDefaultValue) required.Add(name);
            }
            return new Dictionary<string, object?>
            {
                ["name"] = tool.ToolName,
                ["description"] = tool.Description,
                ["parameters"] = new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["properties"] = properties,
                    ["required"] = required,
                },
            };
        }

        private static Dictionary<string, object?> BuildParamSchema(ParameterInfo p)
        {
            var schema = MapType(p.ParameterType);
            var desc = p.GetCustomAttribute<DescriptionAttribute>()?.Description;
            if (IsVerbatim(p))
            {
                schema["verbatim"] = true;
                desc = (string.IsNullOrEmpty(desc) ? "" : desc + " ")
                     + $"VERBATIM: send this as a {PayloadOpen}:{p.Name}» code block, never inside the JSON.";
            }
            if (!string.IsNullOrEmpty(desc)) schema["description"] = desc;
            if (p.HasDefaultValue && p.DefaultValue != null) schema["default"] = p.DefaultValue;
            return schema;
        }

        /// <summary>Map a C# type to a JSON-schema fragment. Complex types collapse to "object" with a
        /// hint to call GetTypeInfo for the full structure, keeping the manifest lean.</summary>
        public static Dictionary<string, object?> MapType(Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (type.IsEnum)
                return new Dictionary<string, object?> { ["type"] = "string", ["enum"] = Enum.GetNames(type) };
            if (type == typeof(string) || type == typeof(char) || type == typeof(Guid))
                return new Dictionary<string, object?> { ["type"] = "string" };
            if (type == typeof(bool))
                return new Dictionary<string, object?> { ["type"] = "boolean" };
            if (type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort)
                || type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong))
                return new Dictionary<string, object?> { ["type"] = "integer" };
            if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
                return new Dictionary<string, object?> { ["type"] = "number" };
            if (type != typeof(string) && TryGetEnumerableElement(type, out var elem))
                return new Dictionary<string, object?> { ["type"] = "array", ["items"] = MapType(elem!) };
            return new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["csharpType"] = FriendlyTypeName(type),
                ["description"] = $"A {FriendlyTypeName(type)} object. Call GetTypeInfo with this type name for its structure.",
            };
        }

        private static bool TryGetEnumerableElement(Type type, out Type? element)
        {
            element = null;
            if (type.IsArray) { element = type.GetElementType(); return element != null; }
            var ienum = type.GetInterfaces().Concat(new[] { type })
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            if (ienum != null) { element = ienum.GetGenericArguments()[0]; return true; }
            return false;
        }

        private static string FriendlyTypeName(Type type)
        {
            if (!type.IsGenericType) return type.Name;
            var name = type.Name.Substring(0, type.Name.IndexOf('`'));
            var args = string.Join(", ", type.GetGenericArguments().Select(FriendlyTypeName));
            return $"{name}<{args}>";
        }

        /// <summary>
        /// Map a named-argument JSON object onto a method's parameters, in declaration order.
        /// Missing optional params take their default; a missing required param or an un-coercible
        /// value is a failure. Pure and DOM-free so it can be unit tested.
        /// </summary>
        public static bool TryBindArguments(ParameterInfo[] parameters, JsonElement args, bool hasArgs,
            out object?[] values, out string? error)
            => TryBindArguments(parameters, args, hasArgs, null, out values, out error);

        /// <summary>Binding overload that also accepts the message's PAYLOAD blocks. A parameter absent from
        /// the JSON args is filled from a payload whose id matches its NAME (name-form), so the model can
        /// simply put a file's contents in a <c>«PAYLOAD:content»</c> block and omit <c>content</c> from
        /// the JSON. An explicit JSON arg wins over a same-named payload - except for a [Verbatim] parameter,
        /// where the payload (its advertised channel) wins.</summary>
        public static bool TryBindArguments(ParameterInfo[] parameters, JsonElement args, bool hasArgs,
            IReadOnlyDictionary<string, string>? payloads, out object?[] values, out string? error)
            // A source with no rendering step in between (a test, an API-driven agent) is exact by
            // construction, so it is treated as fenced. Scraped chat replies go through the ParsedCall overload.
            => TryBindArguments(parameters, args, hasArgs, payloads, true, null, out values, out error);

        /// <summary>Bind a call scraped from a chat page, enforcing <see cref="VerbatimAttribute"/>: a verbatim
        /// parameter takes its value only from a code block (a fenced call, or a fenced PAYLOAD).</summary>
        public static bool TryBindArguments(ParameterInfo[] parameters, ParsedCall call, out object?[] values, out string? error)
            => TryBindArguments(parameters, call.Args, call.HasArgs, call.Payloads, call.Fenced, call.UnfencedPayloads, out values, out error);

        public static bool TryBindArguments(ParameterInfo[] parameters, JsonElement args, bool hasArgs,
            IReadOnlyDictionary<string, string>? payloads, bool callFenced, IReadOnlySet<string>? unfencedPayloads,
            out object?[] values, out string? error)
        {
            values = new object?[parameters.Length];
            error = null;
            for (int i = 0; i < parameters.Length; i++)
            {
                var p = parameters[i];
                var name = p.Name ?? "";
                var verbatim = IsVerbatim(p);
                // For a VERBATIM parameter the PAYLOAD - its advertised channel - wins over an inline JSON value.
                // Live 2026-10-12: Gemini copied the index notation "content*" into the JSON as "content":"*" AND
                // sent the real code as a PAYLOAD; with "JSON wins" the file was written as a single "*".
                var payloadFirst = verbatim && payloads != null && TryGetPayloadIgnoreCase(payloads, name, out _);
                if (!payloadFirst && hasArgs && args.ValueKind == JsonValueKind.Object
                    && TryGetPropertyIgnoreCase(args, name) is JsonElement el)
                {
                    if (verbatim && el.ValueKind == JsonValueKind.String && el.GetString()?.Trim() == "*")
                    {
                        // Our own index notation ("content*" = verbatim) copied in as a placeholder value.
                        error = $"Argument '{name}' was given as \"*\" - the * in the tool list only MARKS '{name}' as verbatim; it is "
                              + $"not a value. Send the real value as a PAYLOAD: {PayloadOpen}:{name}» on its own line, then a ``` "
                              + $"code block holding the exact value, then {PayloadClose}:{name}» on its own line - and leave '{name}' "
                              + "out of the JSON.";
                        return false;
                    }
                    if (verbatim && !callFenced)
                    {
                        error = $"Argument '{name}' must arrive exactly, but this tool call was written outside a code block, "
                              + "where the page's markdown rendering silently strips text such as <...>, backslashes and *emphasis*. "
                              + $"Resend the call inside a ``` code block, and send '{name}' as a PAYLOAD instead of in the JSON: "
                              + $"{PayloadOpen}:{name}» on its own line, then a ``` code block holding the exact value, then "
                              + $"{PayloadClose}:{name}» on its own line.";
                        return false;
                    }
                    try
                    {
                        values[i] = el.Deserialize(p.ParameterType, ArgOptions);
                    }
                    catch (JsonException ex)
                    {
                        error = $"Argument '{name}' could not be converted to {FriendlyTypeName(p.ParameterType)}: {ex.Message}";
                        return false;
                    }
                }
                else if (payloads != null && TryGetPayloadIgnoreCase(payloads, name, out var body))
                {
                    if (verbatim && unfencedPayloads != null && unfencedPayloads.Contains(name))
                    {
                        error = $"The {PayloadOpen}:{name}» block's value was not a single ``` code block, so it went through the "
                              + "page's markdown rendering, which silently strips text such as <...>, backslashes and *emphasis*. "
                              + $"Resend it with the exact value inside ONE ``` code block between {PayloadOpen}:{name}» and "
                              + $"{PayloadClose}:{name}». If the value itself contains ``` lines, open and close its code block "
                              + "with four backticks (````) instead. Never put a code block INSIDE another one: a ``` line inside "
                              + "a code block ENDS that block, so the value falls out into markdown (measured on Z.ai: one block "
                              + "holding the call, the opening marker and a ```csharp line). The layout is three separate parts:\n"
                              + $"```\n{CallOpen}\n{{\"tool\":\"...\",\"args\":{{...}}}}\n{CallClose}\n```\n"
                              + $"{PayloadOpen}:{name}»\n```\n(the exact value)\n```\n{PayloadClose}:{name}»";
                        return false;
                    }
                    try
                    {
                        // A payload is a raw string: assign directly to a string parameter; for any other
                        // type, treat the body as JSON (lets a payload also carry, e.g., a JSON array).
                        values[i] = p.ParameterType == typeof(string) ? body : JsonSerializer.Deserialize(body, p.ParameterType, ArgOptions);
                    }
                    catch (JsonException ex)
                    {
                        error = $"Payload '{name}' could not be converted to {FriendlyTypeName(p.ParameterType)}: {ex.Message}";
                        return false;
                    }
                }
                else if (p.HasDefaultValue)
                {
                    values[i] = p.DefaultValue;
                }
                else
                {
                    error = $"Missing required argument '{name}'.";
                    return false;
                }
            }
            return true;
        }

        /// <summary>True for a parameter marked <see cref="VerbatimAttribute"/>.</summary>
        public static bool IsVerbatim(ParameterInfo p) => p.GetCustomAttribute<VerbatimAttribute>() != null;

        private static bool TryGetPayloadIgnoreCase(IReadOnlyDictionary<string, string> payloads, string name, out string body)
        {
            foreach (var kv in payloads)
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) { body = kv.Value; return true; }
            body = "";
            return false;
        }

        /// <summary>
        /// <paramref name="text"/> cut to its first <paramref name="keep"/> characters plus a note saying what was cut
        /// and how to ask for less. Used when a tool result would push a message past the site's per-message limit
        /// (Google AI Mode: 8192) - the model must know it saw part of the result, never be handed a silent cut.
        /// </summary>
        public static string TruncatedResult(string text, int keep, int limit)
        {
            keep = Math.Clamp(keep, 0, text.Length);
            return text.Substring(0, keep)
                + $"\n…[truncated: {text.Length - keep} more characters not shown - this chat site accepts at most {limit} "
                + "characters per message. Ask for less: e.g. ReadFile with a smaller maxBytes, or SearchContent for the part you need.]";
        }

        /// <summary>Serialize the results array to send back to Gemini.</summary>
        public static string SerializeResults(IEnumerable<object?> results) =>
            JsonSerializer.Serialize(results, ResultOptions);

        public static JsonSerializerOptions ArgSerializerOptions => ArgOptions;
    }
}
