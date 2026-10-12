// Unit harness for the PURE half of the Gemineachy tool pipeline: ToolProtocol (payload extraction,
// tool-call parsing, argument binding, manifest/index/delta building) and VirtualFilePath.
// Compiles the REAL production sources - see ToolTests.csproj. No mocks.
//
//   dotnet run --project _tools/tool-tests -c Release
//
// The DOM half (scraping Gemini's rendered message, which is where the code-block chrome bug lived)
// cannot run here - it is guarded in-browser by ChatAgentService.RunScrapeSelfTest, whose verdict is
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
// What the page scraper (ChatAgentService.ReadMessageText) makes of one rendered code block.
static string Code(string text) => ToolProtocol.CodeStart + text + ToolProtocol.CodeEnd;

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
    // Real shape: a WriteFile call whose content rides the payload channel. The scraper wraps each code
    // block's text in the code-region marks (S..E), which is what makes the payload count as fenced.
    const string code = "class P { static void Main() { System.Console.WriteLine(\"hi\"); } }\n";
    var msg = "«TOOL_CALL {\"tool\":\"FileSystemService.WriteFile\",\"args\":{\"path\":\"/opfs/P.cs\"}} »\n"
            + $"«PAYLOAD:content»\n{Code(code)}\n«/PAYLOAD:content»";
    var calls = ToolProtocol.ParseCalls(msg);
    var ps = typeof(Sample).GetMethod(nameof(Sample.WriteFile))!.GetParameters();
    var ok = ToolProtocol.TryBindArguments(ps, calls[0], out var vals, out var err);
    Check("end-to-end: call + name-form payload binds", ok, err);
    Eq("end-to-end: path bound from JSON", (string?)vals[0], "/opfs/P.cs");
    Eq("end-to-end: content bound verbatim from payload", (string?)vals[1], code);
}

// ---------------------------------------------------------------------------------------------
Console.WriteLine("== Fencing / [Verbatim] ==");
// A scraped chat reply is markdown rendered to DOM. Outside a code block that rendering silently loses
// text: the live 2026-10-12 repro turned List<int> into List and \" into ". These pin that a [Verbatim]
// parameter only ever binds a value that came out of a code block.
var wfParams = typeof(Sample).GetMethod(nameof(Sample.WriteFile))!.GetParameters();
{
    // The silent case from the live repro: unfenced call, JSON still VALID after <int> was stripped.
    var msg = "«TOOL_CALL {\"tool\":\"FileSystemService.WriteFile\",\"args\":{\"path\":\"/opfs/S.cs\",\"content\":\"var d = new Dictionary<string, List>();\"}} »";
    var c = ToolProtocol.ParseCalls(msg)[0];
    Check("unfenced call is reported unfenced", c.ParseError == null && !c.Fenced, c.ParseError);
    var ok = ToolProtocol.TryBindArguments(wfParams, c, out _, out var e);
    Check("verbatim arg from an UNFENCED call is refused", !ok && e != null && e.Contains("content") && e.Contains("PAYLOAD"), e);
}
{
    // The same call inside a code block is exact, so inline JSON content is fine there.
    var json = "{\"tool\":\"FileSystemService.WriteFile\",\"args\":{\"path\":\"/opfs/S.cs\",\"content\":\"List<int> x; s = \\\"q\\\";\"}}";
    var c = ToolProtocol.ParseCalls(Code($"«TOOL_CALL\n{json}\n»"))[0];
    Check("call inside a code block is fenced", c.ParseError == null && c.Fenced, c.ParseError);
    var ok = ToolProtocol.TryBindArguments(wfParams, c, out var v, out var e);
    Eq("fenced inline content binds exactly", ok ? (string?)v[1] : e, "List<int> x; s = \"q\";");
}
{
    // Call marker in prose, JSON alone in a code block - still exact.
    var c = ToolProtocol.ParseCalls("«TOOL_CALL " + Code("{\"tool\":\"X\",\"args\":{}}") + " »")[0];
    Check("JSON that IS one code block counts as fenced", c.ParseError == null && c.Fenced, c.ParseError ?? c.Raw);
    Check("code marks never reach Raw", !c.Raw.Contains(ToolProtocol.CodeStart) && !c.Raw.Contains(ToolProtocol.CodeEnd), c.Raw);
}
{
    // A non-verbatim argument (a path, a chess move) is accepted from an unfenced call - refusing those
    // would only cost round-trips.
    var msg = "«TOOL_CALL {\"tool\":\"FileSystemService.ReadFile\",\"args\":{\"path\":\"/opfs/a.txt\"}} »";
    var c = ToolProtocol.ParseCalls(msg)[0];
    var ok = ToolProtocol.TryBindArguments(typeof(Sample).GetMethod(nameof(Sample.ReadFile))!.GetParameters(), c, out var v, out var e);
    Check("plain arg from an unfenced call still binds", ok && (string?)v[0] == "/opfs/a.txt", e);
}
{
    // Payload body written as prose (no code block): unfenced, so a verbatim param refuses it.
    var msg = "«TOOL_CALL {\"tool\":\"W\",\"args\":{\"path\":\"/opfs/a.cs\"}} »\n«PAYLOAD:content»\nvar d = new Dictionary<string, List>();\n«/PAYLOAD:content»";
    var c = ToolProtocol.ParseCalls(msg)[0];
    Check("prose payload is reported unfenced", c.UnfencedPayloads != null && c.UnfencedPayloads.Contains("content"));
    var ok = ToolProtocol.TryBindArguments(wfParams, c, out _, out var e);
    Check("verbatim arg from an UNFENCED payload is refused", !ok && e != null && e.Contains("four backticks"), e);
}
{
    // Payload markers INSIDE the code block (model fenced the whole thing) - exact, so fenced.
    const string code = "line <a>\n\\\"b\\\"\n";
    var msg = "«TOOL_CALL {\"tool\":\"W\",\"args\":{\"path\":\"/opfs/a.cs\"}} »\n" + Code($"«PAYLOAD:content»\n{code}\n«/PAYLOAD:content»");
    var c = ToolProtocol.ParseCalls(msg)[0];
    var ok = ToolProtocol.TryBindArguments(wfParams, c, out var v, out var e);
    Eq("payload with markers inside the code block binds exactly", ok ? (string?)v[1] : e, code);
}
{
    // The exact shape Gemini produced live (2026-10-12): ONE outer fence around the call and the payload's
    // opening marker; the inner ```csharp line left literal; the inner closing ``` closed the OUTER block;
    // the payload's closing marker in prose. The code is exact - it must bind, minus the literal fence line.
    const string code = "using System;\nvar d = new Dictionary<string, List<int>>();\nConsole.WriteLine($\"a\\\"b\");\n";
    // ReadMessageText puts the code block on its own lines: "\n" + marks + <pre> text + "\n".
    var msg = "\n" + Code("«TOOL_CALL\n{\"tool\":\"W\",\"args\":{\"path\":\"/opfs/a.cs\"}}\n»\n«PAYLOAD:content»\n```csharp\n" + code)
            + "\n«/PAYLOAD:content»";
    var c = ToolProtocol.ParseCalls(msg)[0];
    Check("Gemini's outer-fence shape: call fenced", c.ParseError == null && c.Fenced, c.ParseError);
    var ok = ToolProtocol.TryBindArguments(wfParams, c, out var v, out var e);
    Eq("Gemini's outer-fence shape: content exact, literal ```csharp line dropped", ok ? (string?)v[1] : e, code);
}
{
    // Empty code block = an empty value, still fenced (an empty file is legitimate).
    var p = ToolProtocol.ExtractPayloads("«PAYLOAD:content»\n" + Code("") + "\n«/PAYLOAD:content»", out var unf);
    Check("empty code-block payload is fenced and empty", p["content"] == "" && !unf.Contains("content"), Show(p["content"]));
}
{
    // Prose glued to a code block inside one payload: NOT fenced (the prose part went through rendering).
    ToolProtocol.ExtractPayloads("«PAYLOAD:content»\nsome prose\n" + Code("x") + "\n«/PAYLOAD:content»", out var unf);
    Check("payload of prose + code block is unfenced", unf.Contains("content"));
}
{
    // A Markdown file with its own ``` fences, sent with a 3-backtick outer fence, renders as SEVERAL code
    // blocks with prose between them - the body is no longer one code block, and must be refused.
    var msg = "«PAYLOAD:content»\n" + Code("# Title") + "\nsome *prose*\n" + Code("x = 1") + "\n«/PAYLOAD:content»";
    ToolProtocol.ExtractPayloads(msg, out var unf);
    Check("payload split across code blocks + prose is unfenced", unf.Contains("content"));
}
{
    var msg = "«PAYLOAD:content»\n" + Code("abc\n") + "\n«/PAYLOAD:content»";
    var p = ToolProtocol.ExtractPayloads(msg, out var unf);
    // "abc\n" is the code block's OWN text, so its trailing newline is part of the value (see the
    // "trailing newline preserved" case above); only the marker lines' newlines are stripped.
    Check("fenced payload value carries no code marks", p["content"] == "abc\n" && !unf.Contains("content"), Show(p["content"]));
    Eq("StripCodeMarks removes both marks", ToolProtocol.StripCodeMarks(Code("x")), "x");
    Eq("StripCodeMarks leaves plain text alone", ToolProtocol.StripCodeMarks("plain"), "plain");
}
{
    var c = ToolProtocol.ParseCalls("«TOOL_CALL {\"tool\":\"X\",\"args\":{\"a\":\"say \"hi\"\"}} »")[0];
    Check("unfenced invalid JSON error says to use a code block", c.ParseError != null && c.ParseError.Contains("code block"), c.ParseError);
}
{
    // The trusted overload (no rendering step in between, e.g. a test or an API agent) is unaffected.
    var ok = ToolProtocol.TryBindArguments(wfParams, Json("{\"path\":\"/a\",\"content\":\"c\"}"), true, out _, out var e);
    Check("trusted-source bind still accepts inline verbatim", ok, e);
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
    // Non-verbatim parameter: an explicit JSON arg beats a same-named payload.
    var noteParams = typeof(Sample).GetMethod(nameof(Sample.Note))!.GetParameters();
    var payloads = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["text"] = "PAYLOADVALUE" };
    var ok = ToolProtocol.TryBindArguments(noteParams, Json("{\"text\":\"JSONVALUE\"}"), true, payloads, out var v, out var e);
    Eq("explicit JSON arg beats a same-named payload (non-verbatim)", (string?)v[0], "JSONVALUE");
    Check("bind with both sources succeeds", ok, e);
}
{
    // Verbatim parameter: the PAYLOAD - its advertised channel - wins over an inline JSON value.
    var payloads = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["content"] = "PAYLOADVALUE" };
    var ok = ToolProtocol.TryBindArguments(writeParams, Json("{\"path\":\"/a\",\"content\":\"JSONVALUE\"}"), true, payloads, out var v, out var e);
    Eq("verbatim param: payload beats inline JSON", ok ? (string?)v[1] : e, "PAYLOADVALUE");
}
{
    // Gemini, live 2026-10-12: copied the index notation into the JSON as "content":"*" AND sent the PAYLOAD.
    // The file was written as a single "*". The payload must win.
    const string code = "using System;\nvar xs = new List<int>();\n";
    var msg = "\n" + Code("«TOOL_CALL\n{\"tool\":\"W\",\"args\":{\"path\":\"/opfs/S.cs\",\"content\":\"*\"}}\n»\n«PAYLOAD:content»\n```csharp\n" + code)
            + "\n«/PAYLOAD:content»";
    var c = ToolProtocol.ParseCalls(msg)[0];
    var ok = ToolProtocol.TryBindArguments(writeParams, c, out var v, out var e);
    Eq("\"content\":\"*\" + PAYLOAD binds the payload", ok ? (string?)v[1] : e, code);
    // "*" alone (no payload) is our notation leaking in as a value - refuse, never write "*".
    var lone = ToolProtocol.ParseCalls(Code("«TOOL_CALL\n{\"tool\":\"W\",\"args\":{\"path\":\"/opfs/S.cs\",\"content\":\"*\"}}\n»"))[0];
    var ok2 = ToolProtocol.TryBindArguments(writeParams, lone, out _, out var e2);
    Check("\"content\":\"*\" without a payload is refused", !ok2 && e2 != null && e2.Contains("not a value"), e2);
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
    // The note can be the model's ONLY sight of the protocol (fresh chat -> ListTools -> call), so it must
    // carry the fencing + PAYLOAD rules itself - their absence is what produced the corrupted C# write.
    Check("first-message note carries the PAYLOAD rule", note.Contains(ToolProtocol.PayloadOpen) && note.Contains(ToolProtocol.PayloadClose));
    Check("first-message note says to fence the call", note.Contains("```") && note.Contains(ToolProtocol.CallOpen));
}
{
    var idx = ToolProtocol.BuildToolIndex(tools);
    Check("index lists argument names, * = verbatim, ? = optional", idx.Contains("Sample.WriteFile(path, content*, append?)"), idx);
    Check("manifest explains the index notation", ToolProtocol.BuildManifest(tools).Contains(ToolProtocol.IndexLegend));
    var schema = ToolProtocol.SerializeSchemas(new[] { tools[0] });
    Check("schema flags the verbatim parameter", schema.Contains("\"verbatim\":true") && schema.Contains("VERBATIM"), schema);
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
    public string WriteFile(string path, [Verbatim] string content, bool append = false) => "";
    [AgentTool("Read a text file.")]
    public string ReadFile(string path, int maxBytes = 65536) => "";
    [AgentTool("Attach labels.")]
    public string Tag(string[] names) => "";
    [AgentTool("Leave a note.")]
    public string Note(string text) => "";
}
