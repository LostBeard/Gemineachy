// Unit harness for the PURE half of the Gemineachy tool pipeline: ToolProtocol (payload extraction,
// tool-call parsing, argument binding, manifest/index/delta building) and VirtualFilePath.
// Compiles the REAL production sources - see ToolTests.csproj. No mocks.
//
//   dotnet run --project _tools/tool-tests -c Release
//
// The DOM half (scraping Gemini's rendered message, which is where the code-block chrome bug lived)
// cannot run here - it is guarded in-browser by GeminiChatService.RunScrapeSelfTest, whose verdict is
// readable via CDP at document.documentElement.getAttribute('data-gem-scrapetest').
using System.Reflection;
using System.Text.Json;
using Gemineachy;
using Gemineachy.Services;

int pass = 0, fail = 0;
void Check(string name, bool ok, string? detail = null)
{
    if (ok) { pass++; Console.WriteLine($"  PASS  {name}"); }
    else { fail++; Console.WriteLine($"  FAIL  {name}{(detail != null ? " - " + detail : "")}"); }
}
void Eq(string name, string? got, string? want) =>
    Check(name, got == want, $"got {Show(got)} want {Show(want)}");
static string Show(string? s) => s == null ? "(null)" : "\"" + s.Replace("\n", "\\n").Replace("\r", "\\r") + "\"";

// ---------------------------------------------------------------------------------------------
Console.WriteLine("== ExtractPayloads ==");

// The shape the DOM scrape produces once a rendered code block has been reduced to its <pre> text:
// the markers sit on their own lines and the code keeps its own trailing newline.
{
    const string code = "using System;\nConsole.WriteLine(\"Hello, \\\"World\\\"!\");\n";
    var msg = $"Sure.\n«PAYLOAD:content»\n{code}\n«/PAYLOAD:content»\ndone.";
    var p = ToolProtocol.ExtractPayloads(msg);
    Eq("code-block payload is verbatim, trailing newline preserved", p.GetValueOrDefault("content"), code);
    Check("payload keeps escaped quotes exactly", p["content"].Contains("\\\"World\\\""));
}
{
    // A file that does NOT end in a newline must not gain one, and vice versa.
    var msg = "«PAYLOAD:content»\nno trailing newline\n«/PAYLOAD:content»";
    Eq("payload without trailing newline stays without", ToolProtocol.ExtractPayloads(msg).GetValueOrDefault("content"), "no trailing newline");
}
{
    // Renderer left the fence backticks in the text instead of consuming them.
    var msg = "«PAYLOAD:content»\n```csharp\nint x = 1;\n```\n«/PAYLOAD:content»";
    Eq("surviving code fence is stripped", ToolProtocol.ExtractPayloads(msg).GetValueOrDefault("content"), "int x = 1;");
}
{
    var msg = "«PAYLOAD:a»\nAAA\n«/PAYLOAD:a»\n«PAYLOAD:b»\nBBB\n«/PAYLOAD:b»";
    var p = ToolProtocol.ExtractPayloads(msg);
    Check("two payloads in one message", p.Count == 2 && p["a"] == "AAA" && p["b"] == "BBB");
}
{
    var msg = "«PAYLOAD:x»\nfirst\n«/PAYLOAD:x»\n«PAYLOAD:x»\nsecond\n«/PAYLOAD:x»";
    Eq("duplicate id - last block wins", ToolProtocol.ExtractPayloads(msg).GetValueOrDefault("x"), "second");
}
{
    var msg = "«PAYLOAD:Content»\nCASE\n«/PAYLOAD:Content»";
    Eq("payload id lookup is case-insensitive", ToolProtocol.ExtractPayloads(msg).GetValueOrDefault("content"), "CASE");
}
{
    var msg = "«PAYLOAD:content»\nline1\n\nline3\n«/PAYLOAD:content»";
    Eq("interior blank lines survive", ToolProtocol.ExtractPayloads(msg).GetValueOrDefault("content"), "line1\n\nline3");
}
Check("no payload blocks - empty map", ToolProtocol.ExtractPayloads("just prose").Count == 0);
Check("null text - empty map", ToolProtocol.ExtractPayloads(null).Count == 0);

// ---------------------------------------------------------------------------------------------
Console.WriteLine("== ParseCalls ==");
{
    var calls = ToolProtocol.ParseCalls("«TOOL_CALL {\"tool\":\"A.B\",\"args\":{\"move\":\"11-15\"}} »");
    Check("nested args form", calls.Count == 1 && calls[0].ParseError == null && calls[0].Tool == "A.B"
        && calls[0].Args.GetProperty("move").GetString() == "11-15");
}
{
    var calls = ToolProtocol.ParseCalls("«TOOL_CALL {\"tool\":\"A.B\",\"move\":\"11-15\"} »");
    Check("flat args form (what Gemini actually emits)", calls.Count == 1 && calls[0].ParseError == null
        && calls[0].Args.GetProperty("move").GetString() == "11-15");
}
{
    var calls = ToolProtocol.ParseCalls("«TOOL_CALL {\"Tool\":\"A.B\",\"ARGS\":{\"Move\":\"1-2\"}} »");
    Check("tool/args keys are case-insensitive", calls.Count == 1 && calls[0].Tool == "A.B" && calls[0].HasArgs);
}
{
    var calls = ToolProtocol.ParseCalls("a «TOOL_CALL {\"tool\":\"X\"} » b «TOOL_CALL {\"tool\":\"Y\"} » c");
    Check("two calls in one message", calls.Count == 2 && calls[0].Tool == "X" && calls[1].Tool == "Y");
}
{
    // Gemini sometimes substitutes ASCII angle brackets for the guillemets (observed live 2026-08-18,
    // alongside an invented tool name). Tolerated, never advertised.
    var calls = ToolProtocol.ParseCalls("<<TOOL_CALL {\"tool\":\"A.B\",\"args\":{\"move\":\"1-2\"}} >>");
    Check("ASCII <<TOOL_CALL … >> is accepted", calls.Count == 1 && calls[0].ParseError == null
        && calls[0].Tool == "A.B" && calls[0].Args.GetProperty("move").GetString() == "1-2",
        calls.Count == 1 ? calls[0].ParseError : $"{calls.Count} calls");
    Check("ASCII and guillemet calls can share a message",
        ToolProtocol.ParseCalls("«TOOL_CALL {\"tool\":\"X\"} »\n<<TOOL_CALL {\"tool\":\"Y\"} >>").Count == 2);
    // An ASCII close must NOT terminate a guillemet call, or a ">>" in the JSON would truncate it.
    var shift = ToolProtocol.ParseCalls("«TOOL_CALL {\"tool\":\"X\",\"expr\":\"a >> b\"} »");
    Check("a >> inside a guillemet call does not truncate it",
        shift.Count == 1 && shift[0].ParseError == null && shift[0].Args.GetProperty("expr").GetString() == "a >> b",
        shift.Count == 1 ? shift[0].ParseError ?? shift[0].Raw : $"{shift.Count} calls");
    Check("ContainsCallOpen sees both forms",
        ToolProtocol.ContainsCallOpen("x «TOOL_CALL y") && ToolProtocol.ContainsCallOpen("x <<TOOL_CALL y")
        && !ToolProtocol.ContainsCallOpen("plain prose") && !ToolProtocol.ContainsCallOpen(null));
    // The manifest keeps advertising ONE canonical form - tolerance is a safety net, not the contract.
    Check("manifest never advertises the ASCII form",
        !ToolProtocol.BuildManifest(Array.Empty<ToolCall>()).Contains(ToolProtocol.CallOpenAscii));
}
{
    var calls = ToolProtocol.ParseCalls("«TOOL_CALL {\"tool\":\"X\", oops} »");
    Check("malformed JSON reports, never throws", calls.Count == 1 && calls[0].ParseError != null);
    Check("malformed JSON error steers to PAYLOAD", calls[0].ParseError!.Contains("PAYLOAD"));
}
Check("non-object JSON reports", ToolProtocol.ParseCalls("«TOOL_CALL [1,2] »")[0].ParseError != null);
Check("missing tool property reports", ToolProtocol.ParseCalls("«TOOL_CALL {\"args\":{}} »")[0].ParseError != null);
Check("no call blocks - empty list", ToolProtocol.ParseCalls("just prose").Count == 0);
{
    // Payloads are message-level: every call in the message sees them.
    var msg = "«TOOL_CALL {\"tool\":\"X\"} »\n«TOOL_CALL {\"tool\":\"Y\"} »\n«PAYLOAD:content»\nCODE\n«/PAYLOAD:content»";
    var calls = ToolProtocol.ParseCalls(msg);
    Check("payloads are shared by every call in the message",
        calls.Count == 2 && calls[0].Payloads?["content"] == "CODE" && calls[1].Payloads?["content"] == "CODE");
}
{
    // Real shape: a WriteFile call whose content rides the payload channel.
    const string code = "class P { static void Main() { System.Console.WriteLine(\"hi\"); } }\n";
    var msg = "«TOOL_CALL {\"tool\":\"FileSystemService.WriteFile\",\"args\":{\"path\":\"/opfs/P.cs\"}} »\n"
            + $"«PAYLOAD:content»\n{code}\n«/PAYLOAD:content»";
    var calls = ToolProtocol.ParseCalls(msg);
    var ps = typeof(Sample).GetMethod(nameof(Sample.WriteFile))!.GetParameters();
    var ok = ToolProtocol.TryBindArguments(ps, calls[0].Args, calls[0].HasArgs, calls[0].Payloads, out var vals, out var err);
    Check("end-to-end: call + name-form payload binds", ok, err);
    Eq("end-to-end: path bound from JSON", (string?)vals[0], "/opfs/P.cs");
    Eq("end-to-end: content bound verbatim from payload", (string?)vals[1], code);
}

// ---------------------------------------------------------------------------------------------
Console.WriteLine("== TryBindArguments ==");
var writeParams = typeof(Sample).GetMethod(nameof(Sample.WriteFile))!.GetParameters();
JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();
{
    var ok = ToolProtocol.TryBindArguments(writeParams, Json("{\"path\":\"/a\",\"content\":\"c\"}"), true, out var v, out var e);
    Check("all args from JSON", ok && (string?)v[0] == "/a" && (string?)v[1] == "c", e);
}
{
    var ok = ToolProtocol.TryBindArguments(writeParams, Json("{\"PATH\":\"/a\",\"Content\":\"c\"}"), true, out var v, out var e);
    Check("argument names are case-insensitive", ok && (string?)v[0] == "/a", e);
}
{
    var payloads = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["content"] = "PAYLOADVALUE" };
    var ok = ToolProtocol.TryBindArguments(writeParams, Json("{\"path\":\"/a\",\"content\":\"JSONVALUE\"}"), true, payloads, out var v, out var e);
    Eq("explicit JSON arg beats a same-named payload", (string?)v[1], "JSONVALUE");
    Check("bind with both sources succeeds", ok, e);
}
{
    var ok = ToolProtocol.TryBindArguments(writeParams, Json("{\"path\":\"/a\"}"), true, out _, out var e);
    Check("missing required arg fails with a named error", !ok && e != null && e.Contains("content"), e);
}
{
    var ok = ToolProtocol.TryBindArguments(writeParams, Json("{\"path\":\"/a\",\"content\":\"c\"}"), true, out var v, out var e);
    Check("defaulted param takes its default", ok && (bool)v[2]! == false, e);
}
{
    var ok = ToolProtocol.TryBindArguments(writeParams, Json("{\"path\":\"/a\",\"content\":\"c\",\"append\":true}"), true, out var v, out var e);
    Check("bool arg coerces", ok && (bool)v[2]! == true, e);
}
{
    var ps = typeof(Sample).GetMethod(nameof(Sample.ReadFile))!.GetParameters();
    var ok = ToolProtocol.TryBindArguments(ps, Json("{\"path\":\"/a\",\"maxBytes\":4096}"), true, out var v, out var e);
    Check("int arg coerces", ok && (int)v[1]! == 4096, e);
    var bad = ToolProtocol.TryBindArguments(ps, Json("{\"path\":\"/a\",\"maxBytes\":\"lots\"}"), true, out _, out var e2);
    Check("un-coercible arg fails with a named error", !bad && e2 != null && e2.Contains("maxBytes"), e2);
}
{
    // A payload can also carry a non-string argument as JSON.
    var ps = typeof(Sample).GetMethod(nameof(Sample.Tag))!.GetParameters();
    var payloads = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["names"] = "[\"a\",\"b\"]" };
    var ok = ToolProtocol.TryBindArguments(ps, Json("{}"), true, payloads, out var v, out var e);
    Check("payload deserializes into a non-string param", ok && ((string[])v[0]!).Length == 2, e);
}

// ---------------------------------------------------------------------------------------------
Console.WriteLine("== Manifest / index / delta ==");
var tools = new List<ToolCall>
{
    Tool(nameof(Sample.WriteFile), "Write a text file. Creates parent directories as needed and overwrites by default."),
    Tool(nameof(Sample.ReadFile),  "Read a text file from the virtual filesystem."),
    Tool(nameof(Sample.Tag),       "Attach labels to something."),
};
ToolCall Tool(string method, string desc)
{
    var mi = typeof(Sample).GetMethod(method)!;
    return new ToolCall($"{nameof(Sample)}.{method}", typeof(Sample), mi, null, desc);
}
{
    var m = ToolProtocol.BuildManifest(tools);
    Check("manifest documents the call format", m.Contains(ToolProtocol.CallOpen));
    Check("manifest documents the PAYLOAD channel", m.Contains(ToolProtocol.PayloadOpen));
    Check("manifest documents the hidden-reply marker", m.Contains(ToolProtocol.HiddenResponseMarker));
    Check("manifest documents discovery", m.Contains(ToolProtocol.SearchToolsName) && m.Contains(ToolProtocol.GetToolSchemaName));
    Check("manifest lists every tool by name", tools.All(t => m.Contains(t.ToolName)));
    // The whole point of the lean manifest: names + summaries, NOT argument schemas.
    Check("lean manifest carries NO argument schemas", !m.Contains("\"properties\""));
}
{
    var idx = ToolProtocol.BuildToolIndex(tools);
    Check("index has one line per tool", idx.Split('\n').Length == tools.Count);
    Check("index trims a description to its first sentence",
        idx.Contains("Write a text file.") && !idx.Contains("overwrites by default"));
    Eq("empty index reads (none)", ToolProtocol.BuildToolIndex(new List<ToolCall>()), "(none)");
}
{
    var hits = ToolProtocol.MatchTools(tools, "file");
    Check("search matches name+description", hits.Count == 2);
    Check("empty query returns all", ToolProtocol.MatchTools(tools, "").Count == tools.Count);
    Check("no match returns none", ToolProtocol.MatchTools(tools, "zzzz").Count == 0);
    var ranked = ToolProtocol.MatchTools(tools, "read file");
    Check("results rank by distinct term hits", ranked.Count > 0 && ranked[0].ToolName.Contains("ReadFile"));
}
{
    var msg = ToolProtocol.BuildToolChangeMessage(new[] { tools[0] }, new[] { "Old.Tool" }, "Game is set up.");
    Check("delta names the added tool", msg.Contains(tools[0].ToolName));
    Check("delta names the removed tool", msg.Contains("Old.Tool"));
    Check("delta carries the addendum", msg.Contains("Game is set up."));
    Check("delta re-states the call format", msg.Contains(ToolProtocol.CallOpen));
}
{
    var schemas = ToolProtocol.SerializeSchemas(new[] { tools[0] });
    Check("on-demand schema has argument properties", schemas.Contains("properties") && schemas.Contains("content"));
    var schema = ToolProtocol.BuildToolSchema(tools[0]);
    var required = JsonSerializer.Serialize(schema);
    // Rule: a parameter is required iff it has no default - which is exactly what binding enforces.
    Check("required = params without a default", required.Contains("\"path\"") && required.Contains("\"content\"")
        && !required.Split("\"required\"")[1].Split(']')[0].Contains("append"));
}
{
    var note = ToolProtocol.BuildFirstMessageToolNote();
    Check("first-message note is marked for hiding", note.StartsWith(ToolProtocol.FirstNoteMarker));
    Check("first-message note points at discovery", note.Contains(ToolProtocol.ListToolsName));
}

// ---------------------------------------------------------------------------------------------
Console.WriteLine("== VirtualFilePath ==");
void Norm(string name, string? path, string? expect)
{
    var ok = VirtualFilePath.TryNormalize(path, out var segs, out var err);
    if (expect == null) Check(name, !ok, $"expected rejection, got '{VirtualFilePath.ToDisplay(segs)}' err={err}");
    else Eq(name, ok ? VirtualFilePath.ToDisplay(segs) : $"REJECTED({err})", expect);
}
Norm("root slash", "/", "/");
Norm("empty is root", "", "/");
Norm("simple path", "/opfs/a.txt", "/opfs/a.txt");
Norm("backslashes normalize", "\\opfs\\a.txt", "/opfs/a.txt");
Norm("double slashes collapse", "/opfs//sub///a.txt", "/opfs/sub/a.txt");
Norm("dot segments drop", "/opfs/./a.txt", "/opfs/a.txt");
Norm("dotdot resolves inside", "/opfs/sub/../a.txt", "/opfs/a.txt");
Norm("dotdot cannot escape the root", "/opfs/../../etc", null);
Norm("dotdot cannot escape from root", "/..", null);
Check("root has no mount", VirtualFilePath.Mount(new List<string>()) == "");
{
    VirtualFilePath.TryNormalize("/opfs/sub/a.txt", out var segs, out _);
    Eq("mount is the first segment", VirtualFilePath.Mount(segs), "opfs");
    Eq("name is the last segment", VirtualFilePath.Name(segs), "a.txt");
    Eq("within-mount drops the mount", string.Join('/', VirtualFilePath.WithinMount(segs)), "sub/a.txt");
}
Check("valid name accepted", VirtualFilePath.IsValidName("a.txt"));
Check("name with slash rejected", !VirtualFilePath.IsValidName("a/b"));
Check("empty name rejected", !VirtualFilePath.IsValidName(""));
{
    var rx = new System.Text.RegularExpressions.Regex(VirtualFilePath.GlobToRegex("*.cs"));
    Check("glob matches", rx.IsMatch("Program.cs") && !rx.IsMatch("Program.txt"));
}

// ---------------------------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine($"== {pass}/{pass + fail} passed, {fail} failed ==");
return fail == 0 ? 0 : 1;

/// <summary>Stand-in tool surface: only its SIGNATURES are used (reflection over real ParameterInfo),
/// mirroring FileSystemService's tools. The bodies never run.</summary>
public class Sample
{
    [AgentTool("Write a text file.")]
    public string WriteFile(string path, string content, bool append = false) => "";
    [AgentTool("Read a text file.")]
    public string ReadFile(string path, int maxBytes = 65536) => "";
    [AgentTool("Attach labels.")]
    public string Tag(string[] names) => "";
}
