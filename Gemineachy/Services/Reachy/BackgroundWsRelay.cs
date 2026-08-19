using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using SpawnDev;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;
using SpawnDev.SpawnJS.BrowserExtension;
using SpawnDev.SpawnJS.BrowserExtension.Services;
using SpawnDev.Reachy;

namespace Gemineachy.Services.Reachy
{
    // The robot's WebRTC signalling server is plain ws:// on :8443. The Gemini page is https, so a socket
    // opened from the content script is mixed-content blocked - measured 2026-08-18: the page's WebSocket
    // goes straight to readyState 3, while the extension's BACKGROUND service worker connects and receives
    // {"type":"welcome","peerId":"..."}. So the SIGNALLING is relayed through the worker (same pattern as
    // the HTTP relay above it), while the WebRTC MEDIA still flows content-script <-> robot directly:
    // RTCPeerConnection is not CORS- or mixed-content-gated, and keeping media out of the worker avoids
    // pushing audio frames through extension messaging.

    /// <summary>Relay request, content -> background. One message per operation on one socket.</summary>
    public record WsRelayRequest(string Type, string Op, string Id, string? Url, string? Text);

    /// <summary>Relay response, background -> content. <c>Text</c> null with <c>Closed</c> false is an idle
    /// poll timeout (nothing arrived), which the caller answers by polling again.</summary>
    public record WsRelayResponse(bool Ok, string? Error, string? Text, bool Closed);

    [JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(WsRelayRequest))]
    [JsonSerializable(typeof(WsRelayResponse))]
    internal partial class WsRelayJson : JsonSerializerContext;

    internal static class WsRelayProtocol
    {
        public const string Type = "reachy-ws-relay";
        public const string OpOpen = "open";
        public const string OpSend = "send";
        public const string OpRecv = "recv";
        public const string OpClose = "close";

        /// <summary>
        /// How long the background holds an unanswered <c>recv</c> before returning "nothing yet".
        /// Two constraints set this. It must stay under Chrome's 30s service-worker idle timeout so the
        /// worker is not torn down between polls; and an in-flight message keeps the worker alive, so a
        /// poll that is always outstanding doubles as the keep-alive for the whole signalling session -
        /// which matters because signalling goes quiet once media is flowing.
        /// </summary>
        public const int RecvTimeoutMs = 25_000;
    }

    /// <summary>
    /// Content-side <see cref="ISignalingSocket"/> that performs every operation in the background service
    /// worker. Injected into <see cref="GstSignallingClient"/>/<c>RoseAudioLink</c> in place of the desktop
    /// <see cref="ClientWebSocketSignalingSocket"/>, so the unchanged library talks the same protocol.
    /// </summary>
    public sealed class RelayedSignalingSocket : ISignalingSocket
    {
        private readonly BrowserExtensionService _bes;
        private readonly string _id = Guid.NewGuid().ToString("N");
        private bool _open;

        public RelayedSignalingSocket(BrowserExtensionService bes) => _bes = bes;

        public bool IsOpen => _open;

        public async Task ConnectAsync(Uri uri, CancellationToken ct = default)
        {
            var r = await CallAsync(WsRelayProtocol.OpOpen, uri.ToString(), null);
            if (!r.Ok) throw new InvalidOperationException($"Relayed signalling connect failed: {r.Error}");
            _open = true;
        }

        public async Task SendTextAsync(string text, CancellationToken ct = default)
        {
            var r = await CallAsync(WsRelayProtocol.OpSend, null, text);
            if (!r.Ok) throw new InvalidOperationException($"Relayed signalling send failed: {r.Error}");
        }

        public async Task<string?> ReceiveTextAsync(CancellationToken ct = default)
        {
            // The relay answers a poll with a message, "closed", or "nothing yet" (an idle timeout). Only
            // the last one loops - so this method blocks until there is genuinely something to return,
            // exactly like the direct socket's ReceiveAsync.
            while (!ct.IsCancellationRequested)
            {
                var r = await CallAsync(WsRelayProtocol.OpRecv, null, null);
                if (!r.Ok) throw new InvalidOperationException($"Relayed signalling receive failed: {r.Error}");
                if (r.Closed) { _open = false; return null; }
                if (r.Text != null) return r.Text;
            }
            return null;
        }

        public async Task CloseAsync(CancellationToken ct = default)
        {
            if (!_open) return;
            _open = false;
            try { await CallAsync(WsRelayProtocol.OpClose, null, null); }
            catch (Exception ex) { Console.WriteLine($"[ReachyWs] close failed: {ex.Message}"); }
        }

        public async ValueTask DisposeAsync() => await CloseAsync();

        private async Task<WsRelayResponse> CallAsync(string op, string? url, string? text)
        {
            var runtime = _bes.Runtime
                ?? throw new InvalidOperationException("Extension runtime unavailable (not running as an extension?).");
            var req = new WsRelayRequest(WsRelayProtocol.Type, op, _id, url, text);
            var json = JsonSerializer.Serialize(req, WsRelayJson.Default.WsRelayRequest);
            var respJson = await RelayProtocol.SendWithWakeRetryAsync(runtime, json);
            if (string.IsNullOrEmpty(respJson))
                throw new InvalidOperationException("No response from background WS relay (is the worker alive?).");
            return JsonSerializer.Deserialize(respJson, WsRelayJson.Default.WsRelayResponse)
                   ?? throw new InvalidOperationException("Malformed WS relay response.");
        }
    }

    /// <summary>
    /// Background-side listener (service worker only): owns the real <c>ws://</c> sockets and services the
    /// content side's open/send/recv/close messages. Auto-starts as an <see cref="IAsyncBackgroundService"/>.
    /// </summary>
    public class WsRelayBackgroundService : IAsyncBackgroundService
    {
        public Task Ready => _ready ??= InitAsync();
        private Task? _ready;

        private readonly SpawnJSRuntime _js;
        private readonly BrowserExtensionService _bes;
        // One entry per content-side RelayedSignalingSocket, keyed by the id it generated.
        private readonly ConcurrentDictionary<string, ClientWebSocketSignalingSocket> _sockets = new();

        public WsRelayBackgroundService(SpawnJSRuntime js, BrowserExtensionService bes)
        {
            _js = js;
            _bes = bes;
        }

        private Task InitAsync()
        {
            if (_bes.ExtensionMode != ExtensionMode.Background) return Task.CompletedTask;
            var runtime = _bes.Runtime;
            if (runtime == null) { Console.WriteLine("[ReachyWs] no runtime in background"); return Task.CompletedTask; }
            runtime.OnMessage += OnMessage;
            // NOTE finalizeAsyncStartup() is called by HttpRelayBackgroundService; it is idempotent (it
            // returns immediately once startup has been finalized) so it is not called a second time here.
            Console.WriteLine("[ReachyWs] background ws relay listening");
            return Task.CompletedTask;
        }

        // Returns true to keep the message channel open for the asynchronous sendResponse.
        private bool OnMessage(SpawnJSObject data, MessageSender sender, Function? sendResponse)
        {
            if (sendResponse == null) return false;
            string raw;
            try { raw = data.JSRef!.As<string>(); }
            catch { return false; } // not a string message - not ours
            if (string.IsNullOrEmpty(raw) || !raw.Contains(WsRelayProtocol.Type)) return false;

            WsRelayRequest? req;
            try { req = JsonSerializer.Deserialize(raw, WsRelayJson.Default.WsRelayRequest); }
            catch { return false; }
            if (req == null || req.Type != WsRelayProtocol.Type) return false;

            _ = RespondAsync(req, sendResponse);
            return true;
        }

        private async Task RespondAsync(WsRelayRequest req, Function sendResponse)
        {
            WsRelayResponse result;
            try { result = await HandleAsync(req); }
            catch (Exception ex)
            {
                // Include the type and the throwing frame: the message alone ("Arg_PlatformNotSupported")
                // does not say WHICH call is unsupported in this scope, and the background worker's own
                // console is not where anyone is looking when a relayed call fails.
                var frame = (ex.StackTrace ?? "").Split('\n').FirstOrDefault()?.Trim() ?? "";
                Console.WriteLine($"[ReachyWs] {req.Op} failed: {ex}");
                result = new WsRelayResponse(false, $"{ex.GetType().Name}: {ex.Message} [{frame}]", null, false);
            }
            try
            {
                var json = JsonSerializer.Serialize(result, WsRelayJson.Default.WsRelayResponse);
                sendResponse.CallVoid(null, json);
            }
            catch (Exception ex) { Console.WriteLine($"[ReachyWs] sendResponse failed: {ex.Message}"); }
            finally { sendResponse.Dispose(); }
        }

        private async Task<WsRelayResponse> HandleAsync(WsRelayRequest req)
        {
            switch (req.Op)
            {
                case WsRelayProtocol.OpOpen:
                    {
                        if (string.IsNullOrEmpty(req.Url)) return new WsRelayResponse(false, "open requires a url", null, false);
                        await CloseAndRemoveAsync(req.Id);   // reconnecting on the same id replaces the socket
                        var sock = new ClientWebSocketSignalingSocket();
                        await sock.ConnectAsync(new Uri(req.Url!));
                        _sockets[req.Id] = sock;
                        Console.WriteLine($"[ReachyWs] opened {req.Url}");
                        return new WsRelayResponse(true, null, null, false);
                    }
                case WsRelayProtocol.OpSend:
                    {
                        if (!_sockets.TryGetValue(req.Id, out var sock)) return new WsRelayResponse(false, "unknown socket id", null, false);
                        await sock.SendTextAsync(req.Text ?? "");
                        return new WsRelayResponse(true, null, null, false);
                    }
                case WsRelayProtocol.OpRecv:
                    {
                        if (!_sockets.TryGetValue(req.Id, out var sock)) return new WsRelayResponse(false, "unknown socket id", null, false);
                        using var cts = new CancellationTokenSource(WsRelayProtocol.RecvTimeoutMs);
                        try
                        {
                            var text = await sock.ReceiveTextAsync(cts.Token);
                            if (text == null) { await CloseAndRemoveAsync(req.Id); return new WsRelayResponse(true, null, null, true); }
                            return new WsRelayResponse(true, null, text, false);
                        }
                        catch (OperationCanceledException) when (cts.IsCancellationRequested)
                        {
                            // Idle: nothing arrived inside the poll window. Reported as "nothing yet" so the
                            // content side polls again - the socket itself is untouched and still open.
                            return new WsRelayResponse(true, null, null, false);
                        }
                    }
                case WsRelayProtocol.OpClose:
                    await CloseAndRemoveAsync(req.Id);
                    return new WsRelayResponse(true, null, null, true);
                default:
                    return new WsRelayResponse(false, $"unknown op '{req.Op}'", null, false);
            }
        }

        private async Task CloseAndRemoveAsync(string id)
        {
            if (!_sockets.TryRemove(id, out var sock)) return;
            try { await sock.CloseAsync(); } catch { }
            try { await sock.DisposeAsync(); } catch { }
        }
    }
}
