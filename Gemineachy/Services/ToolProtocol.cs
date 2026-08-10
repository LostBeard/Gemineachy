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

        private static readonly Regex CallRegex = new Regex(
            Regex.Escape(CallOpen) + @"\s*(.+?)" + Regex.Escape(CallClose),
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
            foreach (Match m in CallRegex.Matches(responseText))
            {
                var raw = m.Groups[1].Value.Trim();
                var call = new ParsedCall { Raw = raw };
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
                        // Prefer an explicit args object; otherwise fall back to the flat form.
                        if (TryGetPropertyIgnoreCase(root, "args") is JsonElement argsEl
                            && argsEl.ValueKind == JsonValueKind.Object)
                        {
                            call.Args = argsEl.Clone();
                        }
                        else
                        {
                            var bag = new Dictionary<string, JsonElement>();
                            foreach (var prop in root.EnumerateObject())
                            {
                                if (string.Equals(prop.Name, "tool", StringComparison.OrdinalIgnoreCase)) continue;
                                if (string.Equals(prop.Name, "args", StringComparison.OrdinalIgnoreCase)) continue;
                                bag[prop.Name] = prop.Value.Clone(); // last-writer-wins on dup keys, like JSON
                            }
                            call.Args = JsonSerializer.SerializeToElement(bag, ArgOptions);
                        }
                        // An args object (possibly empty) is always available now.
                        call.HasArgs = call.Args.ValueKind == JsonValueKind.Object;
                    }
                }
                catch (JsonException ex)
                {
                    call.ParseError = $"Invalid tool-call JSON: {ex.Message}";
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
            sb.AppendLine("When you want to call a tool, emit a block EXACTLY like this in your reply, as plain text (do NOT wrap it in a code fence):");
            sb.AppendLine();
            sb.AppendLine(CallOpen);
            sb.AppendLine("{\"tool\":\"<tool name>\",\"args\":{ ...named arguments... }}");
            sb.AppendLine(CallClose);
            sb.AppendLine();
            sb.AppendLine("- Use the exact tool name from the index.");
            sb.AppendLine("- \"args\" is an object of NAMED arguments matching the tool's parameters. Omit optional arguments to accept their defaults; use {} when the tool takes no arguments.");
            sb.AppendLine("- You may emit multiple tool-call blocks in one reply; each block is one call and they run in order.");
            sb.AppendLine($"- After emitting tool calls, stop and wait. The extension runs them and replies with a {ResultsMarker} user message containing the results, then you continue.");
            sb.AppendLine();
            sb.AppendLine("## Results");
            sb.AppendLine($"Results arrive as a {ResultsMarker} user message with the results JSON inlined in the message body: an array, in call order, of objects shaped {{\"tool\":string,\"ok\":bool,\"result\":any}} on success or {{\"tool\":string,\"ok\":false,\"error\":string}} on failure.");
            sb.AppendLine();
            sb.AppendLine("## Hiding your own reply");
            sb.AppendLine($"You can suppress a reply from the user's view by starting it with {HiddenResponseMarker} (put it first so nothing flashes on screen). Use this when the user asks you not to narrate, or when a reply is purely mechanical. The user can still reveal hidden messages with the extension's \"Show tool calls\" toggle. Default to replying normally (visible) unless the user has asked otherwise.");
            sb.AppendLine();
        }

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

        /// <summary>Serialize the results array to send back to Gemini.</summary>
        public static string SerializeResults(IEnumerable<object?> results) =>
            JsonSerializer.Serialize(results, ResultOptions);

        public static JsonSerializerOptions ArgSerializerOptions => ArgOptions;
    }
}
