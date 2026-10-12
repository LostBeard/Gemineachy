// cdp.cs - minimal Chrome DevTools Protocol eval runner for the Gemineachy debug Chrome (port 9222).
//
// Usage: dotnet run cdp.cs <targetUrlSubstr> <jsExprOrAtFile> [timeoutMs]
//   <targetUrlSubstr>   substring of the target page url, e.g. "gemini" or "chrome://extensions"
//   <jsExprOrAtFile>    inline JS expression, OR file:path to a .js file. The expression is awaited
//                       (awaitPromise=true) and returned by value, so `(async()=>{...})()` works.
//   [timeoutMs]         evaluate timeout (default 60000).
//
// Companion probes live next to this file (reload-ext.js, etc). Launch the debug Chrome with:
//   chrome.exe --remote-debugging-port=9222 --user-data-dir="C:\Users\TJ\gem-debug-profile"
//              --load-extension="<Gemineachy>\bin\PublishRelease\chrome" https://gemini.google.com/app
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: cdp.cs <urlSubstr> <jsOr@file> [timeoutMs]");
    Console.Error.WriteLine("       cdp.cs --grant <origin> <perm[,perm...]>   e.g. --grant https://gemini.google.com audioCapture");
    Console.Error.WriteLine("       cdp.cs --reset-grants <origin>");
    Console.Error.WriteLine("       cdp.cs --console <urlSubstr> <seconds> [filter]   (all contexts incl. content script; replays buffered)");
    return 1;
}

// --grant / --reset-grants: browser-level permission control (Browser.grantPermissions).
// ⚠️ OBSERVED 2026-08-18: this did NOT suppress the microphone prompt for a content script's
// getUserMedia on https://gemini.google.com. The call returned {"result":{}},
// navigator.permissions.query still reported "prompt", and Chrome prompted anyway - a human answered it.
// Treat this as UNPROVEN for that case and check the OUTCOME (did a dialog appear?) rather than trusting
// the success reply. Kept because it is one call and may work for other permissions or origins.
if (args[0] is "--grant" or "--reset-grants")
{
    using var http0 = new HttpClient();
    string verJson;
    try { verJson = await http0.GetStringAsync("http://localhost:9222/json/version"); }
    catch (Exception ex) { Console.Error.WriteLine($"[cdp] cannot reach 9222: {ex.Message}"); return 3; }
    using var verDoc = JsonDocument.Parse(verJson);
    var browserWs = verDoc.RootElement.GetProperty("webSocketDebuggerUrl").GetString()!;
    var origin = args[1];
    using var bws = new ClientWebSocket();
    using var bcts = new CancellationTokenSource(20000);
    await bws.ConnectAsync(new Uri(browserWs), bcts.Token);
    string body;
    if (args[0] == "--grant")
    {
        if (args.Length < 3) { Console.Error.WriteLine("--grant needs a permission list"); return 1; }
        var perms = string.Join(",", args[2].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(p => "\"" + p.Trim() + "\""));
        body = "{\"id\":1,\"method\":\"Browser.grantPermissions\",\"params\":{\"origin\":\"" + origin + "\",\"permissions\":[" + perms + "]}}";
    }
    else
    {
        body = "{\"id\":1,\"method\":\"Browser.resetPermissions\",\"params\":{\"browserContextId\":null}}"
            .Replace(",\"params\":{\"browserContextId\":null}", "");
    }
    await bws.SendAsync(Encoding.UTF8.GetBytes(body), WebSocketMessageType.Text, true, bcts.Token);
    var rbuf = new byte[1 << 16];
    var rr = await bws.ReceiveAsync(rbuf, bcts.Token);
    Console.WriteLine(Encoding.UTF8.GetString(rbuf, 0, rr.Count));
    await bws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    return 0;
}

// --console <urlSubstr> <seconds> [filter]: print the target's console output, ALL execution contexts -
// including the extension's content-script world, whose console a page-world evaluate cannot see.
// Runtime.enable makes V8 replay the messages it has buffered, so this also shows what was logged BEFORE
// attaching (e.g. why a tool round silently produced no [TOOL_RESULTS] reply), then listens <seconds> more.
bool consoleMode = args[0] == "--console";
if (consoleMode) args = args.Skip(1).ToArray();
// --shot <urlSubstr> <out.png>: Page.captureScreenshot - LOOK at the page when a probe hangs or a send
// silently does nothing (a modal, a sign-in wall, or a JS dialog all look identical from evaluate).
bool shotMode = args[0] == "--shot";
string? shotOut = null;
if (shotMode) { shotOut = args[2]; args = new[] { args[1], "1" }; }
// --enter <urlSubstr>: press Enter as a TRUSTED key (Input.dispatchKeyEvent, isTrusted=true) in whatever has
// focus. A/B against a script-dispatched Enter/click: a page that ignores untrusted events reacts only to this.
bool enterMode = args[0] == "--enter";
if (enterMode) args = new[] { args[1], "1" };
string sub = args[0];
// Note: a leading '@' would be swallowed by dotnet's response-file expansion, so use file: instead.
string js = consoleMode ? "" : args[1].StartsWith("file:") ? File.ReadAllText(args[1].Substring(5)) : args[1];
int timeoutMs = consoleMode ? int.Parse(args[1]) * 1000 : args.Length > 2 ? int.Parse(args[2]) : 60000;
string? consoleFilter = consoleMode && args.Length > 2 ? args[2] : null;

using var http = new HttpClient();
string listJson;
try { listJson = await http.GetStringAsync("http://localhost:9222/json"); }
catch (Exception ex) { Console.Error.WriteLine($"[cdp] cannot reach 9222: {ex.Message}"); return 3; }

using var doc = JsonDocument.Parse(listJson);
// Any JS execution context is a valid target, not just pages: the extension's MV3 background is a
// "service_worker" target, and it is the only scope that can answer questions about background-only
// capabilities (LAN ws://, host permissions, relay listeners). A page is preferred when several match,
// so existing probes that pass "gemini" keep hitting the page.
// NOTE an MV3 service worker only EXISTS as a target while it is running. If it is dormant, wake it
// first (reload the extension from chrome://extensions, or send it a message) and re-run.
string? wsUrl = null, chosen = null, chosenType = null;
foreach (var t in doc.RootElement.EnumerateArray())
{
    var url = t.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
    var type = t.TryGetProperty("type", out var ty) ? ty.GetString() ?? "" : "";
    if (type is not ("page" or "service_worker" or "worker" or "shared_worker" or "background_page" or "webview")) continue;
    if (!url.Contains(sub, StringComparison.OrdinalIgnoreCase)) continue;
    if (wsUrl != null && chosenType == "page") continue;      // already have a page - keep it
    wsUrl = t.GetProperty("webSocketDebuggerUrl").GetString();
    chosen = url;
    chosenType = type;
    if (type == "page") continue;                              // a later page cannot beat this one
}
if (wsUrl == null)
{
    Console.Error.WriteLine($"[cdp] no target matching '{sub}'. Targets:");
    foreach (var t in doc.RootElement.EnumerateArray())
        Console.Error.WriteLine($"       {t.GetProperty("type").GetString(),-15} {t.GetProperty("url").GetString()}");
    return 2;
}
Console.Error.WriteLine($"[cdp] target: {chosen} ({chosenType})");

using var ws = new ClientWebSocket();
using var cts = new CancellationTokenSource(timeoutMs + 5000);
await ws.ConnectAsync(new Uri(wsUrl), cts.Token);

async Task Send(string json)
{
    var bytes = Encoding.UTF8.GetBytes(json);
    await ws.SendAsync(bytes, WebSocketMessageType.Text, true, cts.Token);
}
async Task<JsonDocument> Recv()
{
    var buf = new byte[1 << 20]; var sb = new StringBuilder();
    while (true)
    {
        var r = await ws.ReceiveAsync(buf, cts.Token);
        sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
        if (r.EndOfMessage) break;
    }
    return JsonDocument.Parse(sb.ToString());
}

if (enterMode)
{
    await Send("{\"id\":7,\"method\":\"Input.dispatchKeyEvent\",\"params\":{\"type\":\"rawKeyDown\",\"key\":\"Enter\",\"code\":\"Enter\",\"windowsVirtualKeyCode\":13,\"nativeVirtualKeyCode\":13}}");
    await Send("{\"id\":8,\"method\":\"Input.dispatchKeyEvent\",\"params\":{\"type\":\"char\",\"text\":\"\\r\",\"key\":\"Enter\",\"code\":\"Enter\",\"windowsVirtualKeyCode\":13,\"nativeVirtualKeyCode\":13}}");
    await Send("{\"id\":9,\"method\":\"Input.dispatchKeyEvent\",\"params\":{\"type\":\"keyUp\",\"key\":\"Enter\",\"code\":\"Enter\",\"windowsVirtualKeyCode\":13,\"nativeVirtualKeyCode\":13}}");
    for (int got = 0; got < 3;)
    {
        using var msg = await Recv();
        if (msg.RootElement.TryGetProperty("id", out _)) got++;
    }
    Console.WriteLine("enter sent");
    return 0;
}
if (shotMode)
{
    await Send("{\"id\":9,\"method\":\"Page.captureScreenshot\",\"params\":{\"format\":\"png\"}}");
    while (true)
    {
        using var msg = await Recv();
        if (!msg.RootElement.TryGetProperty("id", out var idEl) || idEl.GetInt32() != 9) continue;
        if (msg.RootElement.TryGetProperty("result", out var res) && res.TryGetProperty("data", out var data))
        {
            File.WriteAllBytes(shotOut!, Convert.FromBase64String(data.GetString()!));
            Console.WriteLine($"saved {shotOut}");
            return 0;
        }
        Console.WriteLine("CDP_ERROR: " + msg.RootElement.ToString());
        return 5;
    }
}
await Send("{\"id\":1,\"method\":\"Runtime.enable\"}");
if (consoleMode)
{
    var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
    try
    {
        while (DateTime.UtcNow < until)
        {
            using var msg = await Recv();
            var root = msg.RootElement;
            if (!root.TryGetProperty("method", out var m)) continue;
            string? line = null;
            if (m.GetString() == "Runtime.consoleAPICalled")
            {
                var p = root.GetProperty("params");
                var parts = p.GetProperty("args").EnumerateArray().Select(a =>
                    a.TryGetProperty("value", out var v) ? v.ToString()
                    : a.TryGetProperty("description", out var d) ? d.GetString() : a.ToString());
                line = $"[{p.GetProperty("type").GetString()}] {string.Join(" ", parts)}";
            }
            else if (m.GetString() == "Runtime.exceptionThrown")
                line = "[exception] " + root.GetProperty("params").GetProperty("exceptionDetails").ToString();
            if (line != null && (consoleFilter == null || line.Contains(consoleFilter, StringComparison.OrdinalIgnoreCase)))
                Console.WriteLine(line);
        }
    }
    catch (OperationCanceledException) { }
    try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch { }
    return 0;
}
// Build the evaluate message with the JS expression JSON-encoded as a string literal.
// (Hand-encoded to avoid JsonSerializer reflection, which is disabled in `dotnet run file.cs`.)
static string JsonStr(string s)
{
    var sb = new StringBuilder(s.Length + 2);
    sb.Append('"');
    foreach (var c in s)
        switch (c)
        {
            case '"': sb.Append("\\\""); break;
            case '\\': sb.Append("\\\\"); break;
            case '\b': sb.Append("\\b"); break;
            case '\f': sb.Append("\\f"); break;
            case '\n': sb.Append("\\n"); break;
            case '\r': sb.Append("\\r"); break;
            case '\t': sb.Append("\\t"); break;
            default:
                if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
                break;
        }
    sb.Append('"');
    return sb.ToString();
}
string jsLiteral = JsonStr(js);
string eval = "{\"id\":2,\"method\":\"Runtime.evaluate\",\"params\":{\"expression\":" + jsLiteral +
              ",\"awaitPromise\":true,\"returnByValue\":true,\"userGesture\":true,\"timeout\":" + timeoutMs + "}}";
await Send(eval);

int rc = 0;
while (true)
{
    using var msg = await Recv();
    if (!msg.RootElement.TryGetProperty("id", out var idEl) || idEl.GetInt32() != 2) continue;
    var root = msg.RootElement;
    if (root.TryGetProperty("result", out var res))
    {
        if (res.TryGetProperty("exceptionDetails", out var ex))
        {
            Console.WriteLine("EXCEPTION: " + ex.ToString());
            rc = 4;
        }
        if (res.TryGetProperty("result", out var rv))
        {
            var val = rv.TryGetProperty("value", out var v) ? v.ToString()
                    : rv.TryGetProperty("description", out var d) ? d.GetString() : rv.ToString();
            Console.WriteLine(val);
        }
    }
    else if (root.TryGetProperty("error", out var err))
    {
        Console.WriteLine("CDP_ERROR: " + err.ToString());
        rc = 5;
    }
    break;
}
await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
return rc;
