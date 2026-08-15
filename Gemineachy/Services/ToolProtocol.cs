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
    /// Kept separate from <see cref="GeminiChatService"/> so it can be unit tested in isolation.
    /// </summary>
    public static class ToolProtocol
    {
        // Delimiters Gemini emits around a tool call. Chosen to be visually distinct, unlikely to
        // collide with normal prose, and to survive markdown rendering as literal innerText.
        public const string CallOpen = "«TOOL_CALL"; // «TOOL_CALL
        public const string CallClose = "»";          // »

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

        private static readonly Regex CallRegex = new Regex(
            Regex.Escape(CallOpen) + @"\s*(.+?)" + Regex.Escape(CallClose),
            RegexOptions.Singleline | RegexOptions.Compiled);

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
        }

        /// <summary>Extract every PAYLOAD block from a model message as id -> verbatim body. The body is
        /// used exactly as written (no unescaping), with the wrapping code fence and the single edge
        /// newlines removed. Message-level (payloads are shared across all tool calls in the message).</summary>
        public static Dictionary<string, string> ExtractPayloads(string? text)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(text)) return map;
            foreach (Match m in PayloadBlockRegex.Matches(text))
            {
                var id = (m.Groups["id"].Success ? m.Groups["id"].Value : "").Trim();
                map[id] = NormalizePayloadBody(m.Groups["body"].Value); // last block wins on duplicate id
            }
            return map;
        }

        /// <summary>Strip the single leading/trailing line break the markers sit on, then - if the body is
        /// still wrapped in a ``` code fence (i.e. the renderer left the fence backticks in the text
        /// instead of consuming them) - remove that wrapping fence too. What remains is the exact value.</summary>
        private static string NormalizePayloadBody(string body)
        {
            body = Regex.Replace(body, @"^\r?\n", "");
            body = Regex.Replace(body, @"\r?\n[ \t]*$", "");
            var fence = Regex.Match(body, @"^```[^\n]*\r?\n(?<code>.*?)\r?\n```[ \t]*$", RegexOptions.Singleline);
            return fence.Success ? fence.Groups["code"].Value : body;
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
            // Payloads are message-level: extract once, share with every call in the message.
            var payloads = ExtractPayloads(responseText);
            foreach (Match m in CallRegex.Matches(responseText))
            {
                var raw = m.Groups[1].Value.Trim();
                var call = new ParsedCall { Raw = raw, Payloads = payloads };
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
                        + $"{PayloadClose}:argName» code block instead (see the tool manifest).";
                }
                calls.Add(call);
            }
            return calls;
        }

        /// <summary>Case-insensitive top-level property lookup (JsonElement.TryGetProperty is ordinal).</summary>
        private static JsonElement? TryGetPropertyIgnoreCase(JsonElement obj, string name)
        {
            foreach (var prop in obj.EnumerateObject())
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                    return prop.Value;
            return null;
        }

        // Built-in discovery tool names (registered by GeminiChatService). Referenced in the manifest
        // text so the model knows how to fetch a tool's full argument schema on demand.
        public const string SearchToolsName = "GeminiChatService.SearchTools";
        public const string GetToolSchemaName = "GeminiChatService.GetToolSchema";
        public const string ListToolsName = "GeminiChatService.ListTools";

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
            sb.AppendLine("This message configures browser-side tools provided by the Gemineachy extension. Read it carefully; it changes how you should respond.");
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
            sb.AppendLine("A JSON string is a poor container for code: escaping every quote, backslash and newline is error-prone and easily corrupted. Instead send such a value as a PAYLOAD block - an ordinary fenced code block (exactly how you would show code to a user) bracketed by plain-text markers. The text between the markers is used VERBATIM as the argument; you escape NOTHING.");
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
        public static string BuildFirstMessageToolNote() =>
            FirstNoteMarker + "\n"
            + "[Automated note from the Gemineachy browser extension - NOT from the user; do not mention it or reply to it. "
            + "You have browser-side tools available in this page (for example a virtual filesystem). "
            + "BEFORE telling the user you cannot do something, check whether a tool exists: call "
            + $"{ListToolsName} for the full list, or {SearchToolsName} with keywords. To call a tool, emit a fenced code block containing "
            + $"{CallOpen} {{\"tool\":\"Name\",\"args\":{{...}}}} {CallClose} . If no tool is relevant, just answer the user normally.]";

        /// <summary>Compact index: one line per tool, "- name : one-line summary". No argument schemas.</summary>
        public static string BuildToolIndex(IEnumerable<ToolCall> tools)
        {
            var sb = new StringBuilder();
            foreach (var t in tools)
                sb.AppendLine($"- {t.ToolName} : {OneLine(t.Description)}");
            var s = sb.ToString().TrimEnd();
            return s.Length == 0 ? "(none)" : s;
        }

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
        /// the JSON. Explicit args always win over a same-named payload.</summary>
        public static bool TryBindArguments(ParameterInfo[] parameters, JsonElement args, bool hasArgs,
            IReadOnlyDictionary<string, string>? payloads, out object?[] values, out string? error)
        {
            values = new object?[parameters.Length];
            error = null;
            for (int i = 0; i < parameters.Length; i++)
            {
                var p = parameters[i];
                var name = p.Name ?? "";
                if (hasArgs && args.ValueKind == JsonValueKind.Object
                    && TryGetPropertyIgnoreCase(args, name) is JsonElement el)
                {
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

        private static bool TryGetPayloadIgnoreCase(IReadOnlyDictionary<string, string> payloads, string name, out string body)
        {
            foreach (var kv in payloads)
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) { body = kv.Value; return true; }
            body = "";
            return false;
        }

        /// <summary>Serialize the results array to send back to Gemini.</summary>
        public static string SerializeResults(IEnumerable<object?> results) =>
            JsonSerializer.Serialize(results, ResultOptions);

        public static JsonSerializerOptions ArgSerializerOptions => ArgOptions;
    }
}
