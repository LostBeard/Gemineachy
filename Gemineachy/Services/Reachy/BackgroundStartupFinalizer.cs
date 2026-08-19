using SpawnDev;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.BrowserExtension;
using SpawnDev.SpawnJS.BrowserExtension.Services;

namespace Gemineachy.Services.Reachy
{
    /// <summary>
    /// Releases the runtime events that <c>background.common.js</c> held while .NET was starting - but only
    /// once EVERY background listener has attached.
    /// </summary>
    /// <remarks>
    /// A service worker is torn down when idle and cold-started by the next message, so on that cold start
    /// the waking message is queued by <c>background.common.js</c> and replayed by
    /// <c>finalizeAsyncStartup()</c>. Whoever calls that decides the deadline for attaching listeners:
    /// anything not yet attached misses the replay entirely, and the content side sees
    /// "Could not establish connection. Receiving end does not exist."
    ///
    /// That is exactly what happened. <see cref="HttpRelayBackgroundService"/> used to attach its listener
    /// and immediately finalize, and <see cref="WsRelayBackgroundService"/> documented that it relied on it
    /// doing so. Because the HTTP relay is registered first it also STARTS first, so a cold start whose
    /// waking message was a WS-relay message replayed into a world where only the HTTP listener existed.
    /// The symptom was sharply asymmetric and looked like a WebSocket problem: GetStatus (HTTP) worked from
    /// a sleeping worker, ConnectAudio (WS) failed from a sleeping worker, and BOTH worked while the worker
    /// happened to still be awake.
    ///
    /// Awaiting each relay's <c>Ready</c> here makes the replay independent of registration order, so
    /// adding another background listener later cannot silently reintroduce this.
    /// </remarks>
    public class StartupFinalizerBackgroundService : IAsyncBackgroundService
    {
        public Task Ready => _ready ??= InitAsync();
        private Task? _ready;

        private readonly SpawnJSRuntime _js;
        private readonly BrowserExtensionService _bes;
        private readonly HttpRelayBackgroundService _http;
        private readonly WsRelayBackgroundService _ws;

        public StartupFinalizerBackgroundService(SpawnJSRuntime js, BrowserExtensionService bes,
            HttpRelayBackgroundService http, WsRelayBackgroundService ws)
        {
            _js = js;
            _bes = bes;
            _http = http;
            _ws = ws;
        }

        private async Task InitAsync()
        {
            if (_bes.ExtensionMode != ExtensionMode.Background) return;
            // Every service that registers a runtime.onMessage handler must be awaited here.
            await _http.Ready;
            await _ws.Ready;
            try
            {
                if (_js.Has("finalizeAsyncStartup")) _js.CallVoid("finalizeAsyncStartup");
                Console.WriteLine("[Startup] released held runtime events (all background listeners attached)");
            }
            catch (Exception ex) { Console.WriteLine($"[Startup] finalizeAsyncStartup: {ex.Message}"); }
        }
    }
}
