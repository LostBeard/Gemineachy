using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SpawnDev;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;
using SpawnDev.SpawnJS.BrowserExtension;
using SpawnDev.SpawnJS.BrowserExtension.Services;

namespace Gemineachy.Services.Reachy
{
    // The Gemini page origin cannot fetch the Reachy daemon on the LAN (CORS: the daemon sends no
    // Access-Control-Allow-Origin). The extension's BACKGROUND service worker, however, holds host
    // permissions and can fetch it cross-origin. So every daemon HTTP request the content-side makes is
    // serialized to a JSON string, sent to the background via chrome.runtime messaging, executed there,
    // and the response returned - all transparently behind a normal HttpClient/HttpMessageHandler, so the
    // unchanged ReachyMiniClient works. JSON strings (not marshaled objects) cross the boundary to keep
    // the contract dead simple and marshalling-proof.

    /// <summary>Relay request, content -> background.</summary>
    public record RelayRequest(string Type, string Method, string Url, string? Body, string? ContentType);

    /// <summary>Relay response, background -> content.</summary>
    public record RelayResponse(bool Ok, int Status, string StatusText, string Body, string? ContentType, string? Error);

    [JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(RelayRequest))]
    [JsonSerializable(typeof(RelayResponse))]
    internal partial class RelayJson : JsonSerializerContext;

    internal static class RelayProtocol
    {
        public const string HttpRelayType = "reachy-http-relay";

        /// <summary>
        /// Send a message to the background worker, retrying the one failure that means "the worker was
        /// asleep" rather than "the worker is broken".
        /// </summary>
        /// <remarks>
        /// An MV3 service worker is torn down when idle. Sending to it is what starts it again, but the
        /// very first message can still land before .NET has booted far enough to register the listener,
        /// and Chrome answers "Could not establish connection. Receiving end does not exist." That is
        /// indistinguishable, at the call site, from a genuinely dead extension - so a robot control or an
        /// audio link would simply fail the first time it was used after a quiet minute. Retrying gives the
        /// worker the moment it needs to come up. Only THAT message is retried; a real error propagates
        /// immediately rather than being tried three times.
        /// </remarks>
        public static async Task<string?> SendWithWakeRetryAsync(
            SpawnDev.SpawnJS.BrowserExtension.Runtime runtime, string json, int attempts = 4)
        {
            for (var attempt = 1; ; attempt++)
            {
                try { return await runtime.SendMessage<string>(json); }
                catch (Exception ex) when (attempt < attempts && IsWorkerAsleep(ex))
                {
                    // Back off a little further each time: cold-starting the WASM runtime is not instant.
                    await Task.Delay(150 * attempt);
                }
            }
        }

        private static bool IsWorkerAsleep(Exception ex) =>
            ex.Message.Contains("Receiving end does not exist", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("Could not establish connection", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("message port closed", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Content-side HttpMessageHandler: forwards each request to the background worker to execute, so the
    /// unchanged <see cref="SpawnDev.Reachy.ReachyMiniClient"/> reaches the LAN daemon despite page CORS.
    /// </summary>
    public class BackgroundRelayHttpHandler : HttpMessageHandler
    {
        private readonly BrowserExtensionService _bes;
        public BackgroundRelayHttpHandler(BrowserExtensionService bes) => _bes = bes;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var runtime = _bes.Runtime ?? throw new HttpRequestException("Extension runtime unavailable (not running as an extension?).");
            string? body = request.Content == null ? null : await request.Content.ReadAsStringAsync(ct);
            string? contentType = request.Content?.Headers?.ContentType?.ToString();
            var req = new RelayRequest(RelayProtocol.HttpRelayType, request.Method.Method, request.RequestUri!.ToString(), body, contentType);
            var reqJson = JsonSerializer.Serialize(req, RelayJson.Default.RelayRequest);

            var respJson = await RelayProtocol.SendWithWakeRetryAsync(runtime, reqJson);
            if (string.IsNullOrEmpty(respJson))
                throw new HttpRequestException("No response from background relay (is the extension background worker alive?).");

            var relay = JsonSerializer.Deserialize(respJson, RelayJson.Default.RelayResponse)
                        ?? throw new HttpRequestException("Malformed relay response.");
            if (relay.Error != null && relay.Status == 0)
                throw new HttpRequestException($"Relay fetch failed: {relay.Error}");

            var resp = new HttpResponseMessage((System.Net.HttpStatusCode)relay.Status)
            {
                RequestMessage = request,
                ReasonPhrase = relay.StatusText,
                Content = new StringContent(relay.Body, Encoding.UTF8),
            };
            if (!string.IsNullOrWhiteSpace(relay.ContentType))
            {
                try { resp.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(relay.ContentType); } catch { }
            }
            return resp;
        }
    }

    /// <summary>
    /// Background-side listener (runs only in the extension service worker): receives relayed HTTP
    /// requests over chrome.runtime messaging, performs the real fetch (permitted host, no page CORS),
    /// and returns the response as a JSON string. Auto-starts as an IAsyncBackgroundService.
    /// </summary>
    public class HttpRelayBackgroundService : IAsyncBackgroundService
    {
        public Task Ready => _ready ??= InitAsync();
        private Task? _ready;

        private readonly SpawnJSRuntime _js;
        private readonly BrowserExtensionService _bes;
        private static readonly HttpClient _http = new();

        public HttpRelayBackgroundService(SpawnJSRuntime js, BrowserExtensionService bes)
        {
            _js = js;
            _bes = bes;
        }

        private Task InitAsync()
        {
            if (_bes.ExtensionMode != ExtensionMode.Background)
                return Task.CompletedTask; // only the background worker relays
            var runtime = _bes.Runtime;
            if (runtime == null) { Console.WriteLine("[ReachyRelay] no runtime in background"); return Task.CompletedTask; }
            runtime.OnMessage += OnMessage;
            // NOTE: finalizeAsyncStartup() is deliberately NOT called here. Releasing the held events the
            // instant THIS listener attaches replays them before any later-starting background service has
            // attached its own - which silently broke the ws relay on every cold start. It is now called by
            // StartupFinalizerBackgroundService once every listener is ready.
            Console.WriteLine("[ReachyRelay] background relay listening");
            return Task.CompletedTask;
        }

        // Returns true to keep the message channel open for the asynchronous sendResponse.
        private bool OnMessage(SpawnJSObject data, MessageSender sender, Function? sendResponse)
        {
            if (sendResponse == null) return false;
            string raw;
            try { raw = data.JSRef!.As<string>(); }
            catch { return false; } // not a string message - not ours
            if (string.IsNullOrEmpty(raw) || !raw.Contains(RelayProtocol.HttpRelayType)) return false;

            RelayRequest? req;
            try { req = JsonSerializer.Deserialize(raw, RelayJson.Default.RelayRequest); }
            catch { return false; }
            if (req == null || req.Type != RelayProtocol.HttpRelayType) return false;

            _ = RespondAsync(req, sendResponse);
            return true;
        }

        private async Task RespondAsync(RelayRequest req, Function sendResponse)
        {
            RelayResponse result;
            try
            {
                using var msg = new HttpRequestMessage(new HttpMethod(req.Method), req.Url);
                if (req.Body != null)
                {
                    var ct = string.IsNullOrWhiteSpace(req.ContentType) ? "application/json" : req.ContentType!.Split(';')[0];
                    msg.Content = new StringContent(req.Body, Encoding.UTF8, ct);
                }
                using var r = await _http.SendAsync(msg);
                var respBody = await r.Content.ReadAsStringAsync();
                var respCt = r.Content.Headers.ContentType?.ToString();
                result = new RelayResponse(r.IsSuccessStatusCode, (int)r.StatusCode, r.ReasonPhrase ?? "", respBody, respCt, null);
            }
            catch (Exception ex)
            {
                result = new RelayResponse(false, 0, "", "", null, ex.Message);
            }
            try
            {
                var json = JsonSerializer.Serialize(result, RelayJson.Default.RelayResponse);
                sendResponse.CallVoid(null, json);
            }
            catch (Exception ex) { Console.WriteLine($"[ReachyRelay] sendResponse failed: {ex.Message}"); }
            finally { sendResponse.Dispose(); }
        }
    }
}
