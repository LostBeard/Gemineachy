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

        private class CallDto
        {
            [JsonPropertyName("tool")] public string? Tool { get; set; }
            [JsonPropertyName("args")] public JsonElement Args { get; set; }
        }

        /// <summary>
        /// Extract every tool-call block from a model response. Never throws - malformed blocks are
        /// returned with <see cref="ParsedCall.ParseError"/> set so the caller can report them back
        /// to the model instead of silently dropping them.
        /// </summary>
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
                    var dto = JsonSerializer.Deserialize<CallDto>(raw, ArgOptions);
                    if (dto == null || string.IsNullOrWhiteSpace(dto.Tool))
                    {
                        call.ParseError = "Tool call JSON is missing a non-empty \"tool\" property.";
                    }
                    else
                    {
                        call.Tool = dto.Tool!;
                        call.Args = dto.Args;
                        call.HasArgs = dto.Args.ValueKind == JsonValueKind.Object;
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

        /// <summary>
        /// Build the human/agent-readable manifest text (protocol instructions + JSON-schema array of tools).
        /// </summary>
        public static string BuildManifest(IEnumerable<ToolCall> tools)
        {
            var sb = new StringBuilder();
            sb.AppendLine("This message configures browser-side tools provided by the Gemineachy extension. Read it carefully; it changes how you should respond.");
            sb.AppendLine();
            sb.AppendLine("## Calling a tool");
            sb.AppendLine("When you want to call a tool, emit a block EXACTLY like this in your reply, as plain text (do NOT wrap it in a code fence):");
            sb.AppendLine();
            sb.AppendLine(CallOpen);
            sb.AppendLine("{\"tool\":\"<tool name>\",\"args\":{ ...named arguments... }}");
            sb.AppendLine(CallClose);
            sb.AppendLine();
            sb.AppendLine("- Use the exact tool name from the list below.");
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
            sb.AppendLine("## Available tools");
            sb.AppendLine(SerializeSchemas(tools));
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
            var method = tool.ToolHandler.Method;
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
                if (hasArgs && args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var el))
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
