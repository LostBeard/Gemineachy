using SpawnDev;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;
using SpawnDev.SpawnJS.BrowserExtension.Services;
using SpawnDev.Reachy;
using SpawnDev.RTC;
using SpawnDev.RTC.Browser;

namespace Gemineachy.Services.Reachy
{
    /// <summary>One configured Reachy Mini connection. The <see cref="Client"/> reaches the daemon through
    /// the background HTTP relay, so it works from the CORS-blocked content page.</summary>
    public class ReachyRobot
    {
        public string Name { get; set; } = "";
        public string Host { get; set; } = "reachy-mini.local";
        public int Port { get; set; } = 8000;
        public ReachyMiniClient Client { get; set; } = default!;
        /// <summary>Last observed daemon state string, or an error, from the most recent status poll.</summary>
        public string Status { get; set; } = "unknown";
        public bool Online { get; set; }
        public string Origin => $"http://{Host}:{Port}";
        /// <summary>True when the host is an mDNS *.local name already covered by manifest host_permissions.</summary>
        public bool IsLocalName => Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase);

        /// <summary>Live WebRTC audio session, once connected. Signalling rides the background relay; the
        /// media itself flows page &lt;-&gt; robot directly.</summary>
        public RoseAudioLink? Audio { get; set; }
        /// <summary>Hidden &lt;audio&gt; element playing the robot's microphone. The track is handed to the
        /// element directly, so the audio is decoded and played by the browser and never enters .NET.</summary>
        public HTMLAudioElement? AudioElement { get; set; }
        /// <summary>Last audio-link state (a W3C peer-connection state, or an error).</summary>
        public string AudioState { get; set; } = "";
        public bool AudioConnected => Audio != null;
        /// <summary>The page microphone stream while the user is talking to the robot, else null.</summary>
        public IRTCMediaStream? MicStream { get; set; }
        public bool MicSending => MicStream != null;
        /// <summary>Shared gesture choreography, driving this robot's daemon through the HTTP relay.</summary>
        public ReachyBody? Body { get; set; }
        /// <summary>Rolling window of the robot's microphone while ears are on, so any recent moment can be
        /// transcribed after the fact instead of having to be recorded on cue.</summary>
        public PcmRing? Ears { get; set; }
        public Action<short[]>? EarsSink { get; set; }
        public bool EarsOn => Ears != null;
    }

    /// <summary>
    /// Manages one or more Reachy Mini robots and exposes them to Gemini as tools (so a single Gemini can
    /// orchestrate several robots). Each robot's daemon REST calls go through the background HTTP relay.
    /// Tools register at startup like <see cref="FileSystemService"/>, so they are always available.
    /// </summary>
    public class ReachyService : IAsyncBackgroundService
    {
        public Task Ready => _ready ??= InitAsync();
        private Task? _ready;

        private readonly SpawnJSRuntime _js;
        private readonly GeminiChatService _gemini;
        private readonly BrowserExtensionService _bes;
        private readonly SpeechService _speech;
        private readonly List<ReachyRobot> _robots = new();

        private const string DB_NAME = "gemineachy_reachy";
        private const string STORE = "robots"; // name -> "host:port"
        private const string DefaultHost = "reachy-mini.local";

        public IReadOnlyList<ReachyRobot> Robots => _robots;
        /// <summary>Fires when the robot set or a robot's status changes (the Reachy app subscribes).</summary>
        public event Action? OnChanged;
        private void NotifyChanged() => OnChanged?.Invoke();

        /// <summary>One agent tool invocation, for the Reachy app's "recent activity" view and for verifying
        /// that Gemini actually drove a robot.</summary>
        public record ReachyAction(DateTime Time, string Tool, string Robot, string Result);
        private readonly List<ReachyAction> _actions = new();
        public IReadOnlyList<ReachyAction> RecentActions => _actions;
        private void LogAction(string tool, string robot, string result)
        {
            _actions.Insert(0, new ReachyAction(DateTime.Now, tool, robot, Short(result)));
            if (_actions.Count > 30) _actions.RemoveAt(_actions.Count - 1);
            NotifyChanged();
        }
        /// <summary>Log a tool result and return it (so each [AgentTool] records what the agent did).</summary>
        private string Done(string tool, ReachyRobot r, string result) { LogAction(tool, r.Name, result); return result; }

        public ReachyService(SpawnJSRuntime js, GeminiChatService gemini, BrowserExtensionService bes, SpeechService speech)
        {
            _js = js;
            _gemini = gemini;
            _bes = bes;
            _speech = speech;
        }

        private async Task InitAsync()
        {
            try { _gemini.Register(this); }
            catch (Exception ex) { Console.WriteLine($"[Reachy] tool register failed: {ex.Message}"); }
            try
            {
                await LoadPersistedAsync();
                if (_robots.Count == 0) await AddRobotAsync("reachy", DefaultHost, notifyGemini: false); // seed the documented default (silent)
            }
            catch (Exception ex) { Console.WriteLine($"[Reachy] init load failed: {ex.Message}"); }
        }

        // ---- Robot management (called from the Reachy app) -------------------------------------------

        /// <summary>Add (or update) a robot connection and persist it. Returns a status message.
        /// <paramref name="notifyGemini"/> introduces the tools + tells Gemini a robot is available; pass
        /// false for the silent startup seed (auto-messaging Gemini on load is intrusive).</summary>
        public async Task<string> AddRobotAsync(string name, string host, int port = 8000, bool notifyGemini = true)
        {
            host = (host ?? "").Trim();
            if (string.IsNullOrWhiteSpace(host)) return "Enter a host or IP (e.g. reachy-mini.local).";
            name = string.IsNullOrWhiteSpace(name) ? host : name.Trim();
            name = UniqueName(name, existingAllowed: FindRobot(name));
            var existing = FindRobot(name);
            if (existing != null)
            {
                existing.Host = host;
                existing.Port = port;
                RebuildClient(existing);
            }
            else
            {
                var robot = new ReachyRobot { Name = name, Host = host, Port = port };
                RebuildClient(robot);
                _robots.Add(robot);
            }
            await SavePersistedAsync(name, $"{host}:{port}");
            NotifyChanged();
            _ = RefreshStatusAsync(name); // fire-and-forget status poll
            if (notifyGemini)
            {
                try
                {
                    await _gemini.NotifyToolContext(
                        $"A Reachy Mini robot named '{name}' is now available at {host}:{port}. You can control it with the Reachy tools " +
                        "(WakeUp, Sleep, MoveHead, TurnBody, SetAntennas, GetStatus, ListRobots) - pass robot=\"" + name + "\" to target it (or omit robot if it is the only one).");
                }
                catch (Exception ex) { Console.WriteLine($"[Reachy] notify failed: {ex.Message}"); }
            }
            return $"Robot '{name}' set to {host}:{port}.";
        }

        /// <summary>Tell Gemini which robots are available and how to control them (introduces the tool
        /// manifest the first time). Called when the Reachy app opens so the agent can drive the robots.</summary>
        public async Task AnnounceRobotsAsync()
        {
            if (_robots.Count == 0) return;
            var list = string.Join(", ", _robots.Select(r => $"'{r.Name}' ({r.Origin})"));
            try
            {
                await _gemini.NotifyToolContext(
                    $"Reachy Mini robot control is available. Configured robot(s): {list}. Control them with the Reachy tools " +
                    "(WakeUp, Sleep, MoveHead, TurnBody, SetAntennas, GetStatus, ListRobots); pass robot=\"name\" to target one, or omit robot if there is only one.");
            }
            catch (Exception ex) { Console.WriteLine($"[Reachy] announce failed: {ex.Message}"); }
        }

        public async Task RemoveRobotAsync(string name)
        {
            var robot = FindRobot(name);
            if (robot == null) return;
            robot.Client?.Dispose();
            _robots.Remove(robot);
            try { await DeletePersistedAsync(name); } catch { }
            NotifyChanged();
        }

        /// <summary>Poll a robot's daemon status (or all robots when name is empty), updating Online/Status.</summary>
        public async Task RefreshStatusAsync(string name = "")
        {
            var targets = string.IsNullOrWhiteSpace(name) ? _robots.ToList() : new List<ReachyRobot>(new[] { FindRobot(name)! }.Where(r => r != null));
            foreach (var r in targets)
            {
                try
                {
                    var s = await r.Client.GetStatusAsync();
                    r.Online = s != null;
                    r.Status = s == null ? "no status" : $"{s.State} (v{s.Version})";
                }
                catch (Exception ex) { r.Online = false; r.Status = Short(ex.Message); }
                NotifyChanged();
            }
        }

        /// <summary>Whether a host needs a runtime permission grant (a raw IP/host not covered by
        /// the manifest's http://*.local/* host permission).</summary>
        public static bool NeedsHostPermission(string host) => !host.EndsWith(".local", StringComparison.OrdinalIgnoreCase);

        // ---- Agent tools (Core movement + status) ----------------------------------------------------

        [AgentTool("List the Reachy Mini robots currently configured, with their host and last-known status. Use the robot's name to target it in the other Reachy tools (omit the name if there is only one).")]
        string ListRobots()
        {
            if (_robots.Count == 0) return "No robots are configured. Ask the user to add one in the Reachy app.";
            return "Robots:\n" + string.Join("\n", _robots.Select(r => $"  {r.Name}  ({r.Origin}) - {(r.Online ? "online" : "offline")}, {r.Status}"));
        }

        [AgentTool("Get a Reachy Mini's daemon status (robot name, state, version, IP). `robot` selects which one; omit it if there is only one configured.")]
        async Task<string> GetStatus(string robot = "")
        {
            var (r, err) = Resolve(robot); if (r == null) return err!;
            try
            {
                var s = await r.Client.GetStatusAsync();
                if (s == null) return $"{r.Name}: no status returned.";
                r.Online = true; r.Status = $"{s.State} (v{s.Version})"; NotifyChanged();
                return Done("GetStatus", r, $"{r.Name}: robot='{s.RobotName}' state={s.State} version={s.Version} ip={s.WlanIp} motors={s.BackendStatus?.MotorControlMode}");
            }
            catch (Exception ex) { return Fail(r, ex, "GetStatus"); }
        }

        [AgentTool("Enable or disable the Reachy Mini's motors. Motors MUST be enabled for the robot to physically move - the daemon accepts move commands with motors off but the robot stays still. WakeUp and the movement tools enable motors automatically. Disabling (`enabled=false`) leaves the robot safely limp when unattended - it first lowers the head to the resting pose so nothing drops suddenly, then goes limp. `robot` selects which one; omit if only one.")]
        async Task<string> Motors(bool enabled, string robot = "")
        {
            var (r, err) = Resolve(robot); if (r == null) return err!;
            try
            {
                if (enabled) await r.Client.SetMotorModeAsync(MotorMode.Enabled);
                else await SafeDisableMotorsAsync(r);
                return Done("Motors", r, $"{r.Name}: motors {(enabled ? "enabled" : "disabled (rested to sleep pose first)")}.");
            }
            catch (Exception ex) { return Fail(r, ex, "Motors"); }
        }

        [AgentTool("Play the Reachy Mini's wake-up animation (enables motors and raises the head to come to life). `robot` selects which one; omit if only one.")]
        async Task<string> WakeUp(string robot = "")
        {
            var (r, err) = Resolve(robot); if (r == null) return err!;
            try { await EnsureMotorsAsync(r); await r.Client.WakeUpAsync(); return Done("WakeUp", r, $"{r.Name}: waking up (motors enabled)."); }
            catch (Exception ex) { return Fail(r, ex, "WakeUp"); }
        }

        [AgentTool("Play the Reachy Mini's go-to-sleep animation (lowers the head to rest). `robot` selects which one; omit if only one.")]
        async Task<string> Sleep(string robot = "")
        {
            var (r, err) = Resolve(robot); if (r == null) return err!;
            try { await r.Client.GotoSleepAsync(); return Done("Sleep", r, $"{r.Name}: going to sleep."); }
            catch (Exception ex) { return Fail(r, ex, "Sleep"); }
        }

        [AgentTool("Stop the Reachy Mini's current movement immediately. `robot` selects which one; omit if only one.")]
        async Task<string> Stop(string robot = "")
        {
            var (r, err) = Resolve(robot); if (r == null) return err!;
            try { await r.Client.StopMoveAsync(); return Done("Stop", r, $"{r.Name}: stopped."); }
            catch (Exception ex) { return Fail(r, ex, "Stop"); }
        }

        [AgentTool("Point the Reachy Mini's HEAD. Angles are in radians: `pitch` tilts up(+)/down(-), `yaw` turns left(+)/right(-), `roll` tilts sideways. Small values (~0.3 rad) read as natural. `duration` is the move time in seconds. `robot` selects which one; omit if only one.")]
        async Task<string> MoveHead(double pitch = 0, double yaw = 0, double roll = 0, double duration = 1.0, string robot = "")
        {
            var (r, err) = Resolve(robot); if (r == null) return err!;
            try
            {
                await EnsureMotorsAsync(r);
                await r.Client.GotoAsync(headPose: new XyzRpyPose(Roll: roll, Pitch: pitch, Yaw: yaw), duration: duration);
                return Done("MoveHead", r, $"{r.Name}: head -> pitch={pitch:0.##} yaw={yaw:0.##} roll={roll:0.##} rad over {duration:0.##}s.");
            }
            catch (Exception ex) { return Fail(r, ex, "MoveHead"); }
        }

        [AgentTool("Turn the Reachy Mini's BODY (torso) to face a direction. `yaw` is in radians, left(+)/right(-), clamped by the daemon to about +/-pi and to ~65 degrees relative to the head. `duration` in seconds. `robot` selects which one; omit if only one.")]
        async Task<string> TurnBody(double yaw, double duration = 1.5, string robot = "")
        {
            var (r, err) = Resolve(robot); if (r == null) return err!;
            try { await EnsureMotorsAsync(r); await r.Client.GotoAsync(bodyYaw: yaw, duration: duration); return Done("TurnBody", r, $"{r.Name}: body -> yaw={yaw:0.##} rad over {duration:0.##}s."); }
            catch (Exception ex) { return Fail(r, ex, "TurnBody"); }
        }

        [AgentTool("Set the Reachy Mini's two ANTENNAS. `left` and `right` are joint angles in radians (small values wiggle the antennas expressively). `duration` in seconds. `robot` selects which one; omit if only one.")]
        async Task<string> SetAntennas(double left, double right, double duration = 0.6, string robot = "")
        {
            var (r, err) = Resolve(robot); if (r == null) return err!;
            try { await EnsureMotorsAsync(r); await r.Client.GotoAsync(antennas: (left, right), duration: duration); return Done("SetAntennas", r, $"{r.Name}: antennas -> L={left:0.##} R={right:0.##} rad."); }
            catch (Exception ex) { return Fail(r, ex, "SetAntennas"); }
        }

        // ---- Manual-control helpers for the Reachy app (call the same client paths) -------------------

        public Task ManualWakeAsync(string name) => SafeAsync(name, async c => { await c.SetMotorModeAsync(MotorMode.Enabled); await c.WakeUpAsync(); });
        public Task ManualSleepAsync(string name) => SafeAsync(name, c => c.GotoSleepAsync());
        public Task ManualStopAsync(string name) => SafeAsync(name, c => c.StopMoveAsync());
        public Task ManualMotorsAsync(string name, bool enabled) => SafeAsync(name, async c =>
        {
            if (enabled) await c.SetMotorModeAsync(MotorMode.Enabled);
            else { try { await c.GotoSleepAsync(); await Task.Delay(1800); } catch { } await c.SetMotorModeAsync(MotorMode.Disabled); }
        });
        public Task ManualHeadAsync(string name, double pitch, double yaw, double roll = 0) =>
            SafeAsync(name, async c => { await c.SetMotorModeAsync(MotorMode.Enabled); await c.GotoAsync(headPose: new XyzRpyPose(Roll: roll, Pitch: pitch, Yaw: yaw), duration: 0.8); });
        public Task ManualAntennasAsync(string name, double left, double right) =>
            SafeAsync(name, async c => { await c.SetMotorModeAsync(MotorMode.Enabled); await c.GotoAsync(antennas: (left, right), duration: 0.5); });

        /// <summary>
        /// Connect to the robot's WebRTC signalling server and report what it advertises, without starting a
        /// media session. This is the readiness check for the A/V link: it proves the whole relay path works
        /// (content script -> background service worker -> the robot's plain <c>ws://</c>, which the https
        /// page itself cannot open) and that the robot is actually publishing a producer to connect to.
        /// </summary>
        public async Task<string> TestSignalingAsync(string? name = null, int port = 8443)
        {
            var (r, error) = Resolve(name);
            if (r == null) return error!;
            const string tool = "TestSignaling";
            GstSignallingClient? sig = null;
            try
            {
                sig = new GstSignallingClient(r.Host, port, new RelayedSignalingSocket(_bes));
                await sig.ConnectAsync();
                var producers = await sig.ListProducersAsync();
                var names = producers.Select(p => p.Name is null ? p.Id : $"{p.Name} ({p.Id})").ToList();
                var result = names.Count == 0
                    ? $"{r.Name}: signalling reachable at ws://{r.Host}:{port} (peerId {sig.PeerId}) but the robot is advertising NO producer - its media pipeline is not publishing."
                    : $"{r.Name}: signalling OK at ws://{r.Host}:{port} (peerId {sig.PeerId}); producers: {string.Join(", ", names)}.";
                return Done(tool, r, result);
            }
            catch (Exception ex)
            {
                // Diagnostics are this method's whole job, so report the failure IN FULL rather than through
                // Fail()'s summarised "is the robot reachable?" message - the useful part of a signalling
                // failure is the exact exception, and it is usually longer than the status-line trim.
                r.Status = Short(ex.Message);
                var full = $"{r.Name}: signalling FAILED at ws://{r.Host}:{port} - {ex.GetType().Name}: {ex.Message}";
                LogAction(tool, r.Name, "FAILED: " + Short(ex.Message));
                return full;
            }
            finally { if (sig != null) await sig.DisposeAsync(); }
        }

        // ---- Hearing the robot ------------------------------------------------------------------------

        /// <summary>
        /// Record a fixed window of the robot's microphone and transcribe it, to prove the speech path
        /// end-to-end before any voice-activity detection is in front of it.
        /// </summary>
        /// <remarks>
        /// This taps <see cref="RoseAudioLink.StartPcmCapture"/>, the path the browser host deliberately
        /// leaves off for plain listening - playing audio hands the track to an &lt;audio&gt; element and never
        /// decodes it in .NET, but recognising speech genuinely needs the samples. The two coexist: the
        /// element keeps playing while this runs.
        /// </remarks>
        /// <summary>
        /// Start keeping a rolling window of the robot's microphone, so speech can be transcribed after it
        /// has been spoken rather than on cue.
        /// </summary>
        public string StartEars(string? name = null, int windowSeconds = 30)
        {
            var (r, error) = Resolve(name);
            if (r == null) return error!;
            if (r.Audio == null) return $"{r.Name}: connect the audio link first (Listen), then start ears.";
            if (r.Ears != null) return $"{r.Name}: already listening ({r.Ears.Count / SpeechService.SampleRate}s buffered).";
            // 30s is Whisper's window, so there is no point retaining more than one model input's worth.
            var ring = new PcmRing(SpeechService.SampleRate * windowSeconds);
            Action<short[]> sink = ring.Write;
            r.Ears = ring;
            r.EarsSink = sink;
            r.Audio.OnMicAudio += sink;
            r.Audio.StartPcmCapture();
            NotifyChanged();
            return Done("Ears", r, $"{r.Name}: listening - keeping the last {windowSeconds}s of the robot's microphone.");
        }

        /// <summary>Stop the rolling capture (the audio link and playback are untouched).</summary>
        public string StopEars(string? name = null)
        {
            var (r, error) = Resolve(name);
            if (r == null) return error!;
            if (r.Ears == null) return $"{r.Name}: ears were not on.";
            if (r.Audio != null && r.EarsSink != null) r.Audio.OnMicAudio -= r.EarsSink;
            r.Ears = null;
            r.EarsSink = null;
            NotifyChanged();
            return Done("Ears", r, $"{r.Name}: stopped listening.");
        }

        /// <summary>
        /// One-shot end-to-end speech test: make sure the link is up and the ears are on, let the buffer
        /// fill, then transcribe. Every step reports what it did.
        /// </summary>
        /// <remarks>
        /// Exists because driving this as four separate clicks made the SEQUENCE the fragile part - a step
        /// that silently did not take left the next one with nothing to work on, and the failure looked
        /// like the speech pipeline rather than the choreography. One entry point, one result.
        /// </remarks>
        public async Task<string> RunSpeechSelfTestAsync(string? name = null, int listenSeconds = 8)
        {
            var (r, error) = Resolve(name);
            if (r == null) return error!;
            var steps = new List<string>();

            if (r.Audio == null)
            {
                var connect = await ConnectAudioAsync(r.Name);
                steps.Add(r.Audio == null ? $"link FAILED ({Short(connect)})" : "link up");
                if (r.Audio == null) return string.Join("; ", steps);
            }
            else steps.Add("link already up");

            if (r.Ears == null) { StartEars(r.Name); steps.Add(r.Ears == null ? "ears FAILED" : "ears on"); }
            else steps.Add("ears already on");
            if (r.Ears == null) return string.Join("; ", steps);

            // ALWAYS capture fresh audio. Reusing whatever happened to be in the ring made the test
            // transcribe history - it once reported on audio from minutes earlier, while the person who
            // asked for the test was talking, and read as a speech failure instead of a stale buffer.
            r.Ears.Clear();
            await Task.Delay(TimeSpan.FromSeconds(listenSeconds));
            steps.Add($"recorded {listenSeconds}s fresh");

            var pcm = r.Ears.Snapshot(SpeechService.SampleRate * listenSeconds);
            if (pcm.Length == 0) return string.Join("; ", steps) + "; nothing captured";
            var result = await TranscribeBufferAsync(r, pcm);
            return Done("SpeechSelfTest", r, string.Join("; ", steps) + " -> " + result);
        }

        /// <summary>Transcribe the most recent <paramref name="seconds"/> of what the robot heard.</summary>
        public async Task<string> TranscribeRecentAsync(string? name = null, int seconds = 10)
        {
            var (r, error) = Resolve(name);
            if (r == null) return error!;
            const string tool = "Transcribe";
            if (r.Ears == null) return $"{r.Name}: ears are not on - press Ears first, talk, then transcribe.";
            var pcm = r.Ears.Snapshot(SpeechService.SampleRate * seconds);
            if (pcm.Length == 0) return Done(tool, r, $"{r.Name}: nothing buffered yet.");
            return Done(tool, r, await TranscribeBufferAsync(r, pcm));
        }

        public async Task<string> ListenAndTranscribeAsync(string? name = null, int seconds = 6)
        {
            var (r, error) = Resolve(name);
            if (r == null) return error!;
            const string tool = "Transcribe";
            if (r.Audio == null) return $"{r.Name}: connect the audio link first (Listen), then transcribe.";

            var buffer = new List<short>(SpeechService.SampleRate * seconds);
            void Collect(short[] pcm) { lock (buffer) buffer.AddRange(pcm); }
            try
            {
                r.Audio.OnMicAudio += Collect;
                r.Audio.StartPcmCapture();          // idempotent; a no-op if PCM is already flowing
                await Task.Delay(TimeSpan.FromSeconds(seconds));
            }
            finally { r.Audio.OnMicAudio -= Collect; }

            short[] pcm;
            lock (buffer) pcm = buffer.ToArray();
            if (pcm.Length == 0)
                return Done(tool, r, $"{r.Name}: captured no audio - is the link still up?");

            return Done(tool, r, await TranscribeBufferAsync(r, pcm));
        }

        /// <summary>
        /// Transcribe a buffer and describe the outcome, including the LEVEL of what was heard.
        /// </summary>
        /// <remarks>
        /// The level is not decoration. "Nothing recognised" is ambiguous between a quiet room and a broken
        /// pipeline, and those need opposite fixes - measuring it is what showed the audio was clean
        /// speech at -23.9 dBFS and sent me looking for the real defect instead of blaming the microphone.
        /// </remarks>
        /// <summary>
        /// Close the loop: listen to the robot's microphone, transcribe it, and send what was heard to
        /// Gemini as a normal chat message.
        /// </summary>
        /// <remarks>
        /// Every piece of this already existed separately - the audio link, the PCM ring, Whisper, and
        /// <see cref="AnimateFromChat"/>'s reply-to-gesture hook. What was missing was the one link that
        /// hands the transcription to Gemini, so the robot could hear and could act, but nothing joined the
        /// two.
        ///
        /// The reply is deliberately NOT acted out here. <see cref="AnimateFromChat"/> already subscribes to
        /// <c>OnQueryResponse</c>, so performing it here as well would run every gesture twice. Turn that
        /// toggle on and the robot answers physically; leave it off and this is transcription plus a chat
        /// message.
        ///
        /// Voice OUT is the remaining gap: the daemon has no TTS endpoint (only sounds/upload + play_sound),
        /// so speaking the reply needs a synthesiser we do not have in-house yet.
        /// </remarks>
        public async Task<string> ListenAndAskGeminiAsync(string? name = null, int seconds = 6)
        {
            var (r, error) = Resolve(name);
            if (r == null) return error!;
            const string tool = "ListenAndAsk";

            if (r.Audio == null)
            {
                var connect = await ConnectAudioAsync(r.Name);
                if (r.Audio == null) return Done(tool, r, $"{r.Name}: audio link FAILED ({Short(connect)})");
            }
            if (r.Ears == null) StartEars(r.Name);
            if (r.Ears == null) return Done(tool, r, $"{r.Name}: could not start ears.");

            // Always capture FRESH audio - reusing whatever sat in the ring once transcribed minutes-old
            // history while the person was still talking, which reads as a speech failure rather than a
            // stale buffer.
            r.Ears.Clear();
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            var pcm = r.Ears.Snapshot(SpeechService.SampleRate * seconds);
            if (pcm.Length == 0) return Done(tool, r, $"{r.Name}: nothing captured in {seconds}s.");

            var outcome = await _speech.TranscribeAsync(pcm);
            if (outcome.Error != null)
                return Done(tool, r, $"{r.Name}: transcription FAILED - {outcome.Error}");
            var heard = (outcome.Text ?? "").Trim();
            if (heard.Length == 0) return Done(tool, r, $"{r.Name}: nothing recognised in {seconds}s of audio.");

            var reply = await _gemini.Query(heard);
            return Done(tool, r,
                $"{r.Name}: heard \"{heard}\" ({outcome.ElapsedMs}ms) -> Gemini: \"{Short(reply)}\"");
        }

        private async Task<string> TranscribeBufferAsync(ReachyRobot r, short[] pcm)
        {
            double sumSq = 0; int peak = 0;
            foreach (var s in pcm) { sumSq += (double)s * s; peak = Math.Max(peak, Math.Abs((int)s)); }
            var rms = Math.Sqrt(sumSq / pcm.Length) / 32768.0;
            var rmsDbfs = rms > 0 ? 20 * Math.Log10(rms) : double.NegativeInfinity;
            var seconds = pcm.Length / (double)SpeechService.SampleRate;
            var level = $"level {rmsDbfs:F1} dBFS, peak {peak / 32768.0:F3}";

            var outcome = await _speech.TranscribeAsync(pcm);
            if (outcome.Error != null)
                return $"{r.Name}: transcription FAILED after {seconds:F1}s of audio ({level}) - {outcome.Error}";
            var text = string.IsNullOrWhiteSpace(outcome.Text) ? "(nothing recognised)" : outcome.Text;
            return $"{r.Name}: heard \"{text}\" [{seconds:F1}s audio, {level}, {outcome.ElapsedMs}ms on {_speech.Describe()}] {_speech.ModelInputs}";
        }

        // ---- Acting out Gemini's replies -------------------------------------------------------------

        /// <summary>
        /// While on, every reply Gemini writes is scanned for inline <c>*stage directions*</c> and the
        /// robot acts them out.
        /// </summary>
        public bool AnimateFromChat
        {
            get => _animateFromChat;
            set
            {
                if (_animateFromChat == value) return;
                _animateFromChat = value;
                if (value) _gemini.OnQueryResponse += OnGeminiReplied;
                else _gemini.OnQueryResponse -= OnGeminiReplied;
                NotifyChanged();
            }
        }
        private bool _animateFromChat;

        /// <summary>What Gemini is told when animation is switched on. Mirrors how the desktop companion
        /// asks for physical reactions - the model narrates action inline and the robot performs it.</summary>
        private const string AnimationBrief =
            "A Reachy Mini robot is now acting out your replies physically. It has a head that tilts, nods and "
            + "turns, a rotating body, and two antennas. Write physical reactions inline in asterisks, as stage "
            + "directions, the way a screenplay does - for example: \"*tilts head curiously* That is a good "
            + "question!\" or \"*antennas perk up* I found it!\". Put the action FIRST so the movement lands with "
            + "the words. Keep them short and physical (tilt, nod, shake, lean in, look up, look down, spin, "
            + "bounce, antennas perk/droop/wiggle, turn body). Everything outside the asterisks is what the robot "
            + "says, so never put narration there. Do not mention this arrangement to the user.";

        private void OnGeminiReplied(string query, string response) => _ = PerformFromTextAsync(response);

        /// <summary>
        /// Act out the <c>*stage directions*</c> in a piece of model text.
        /// </summary>
        /// <remarks>
        /// The split and the gesture vocabulary come from the SpawnDev.Reachy library, the same code the
        /// desktop companion runs - a second implementation here would drift from it within a week.
        /// Gestures are fired and NOT awaited, deliberately: in the desktop companion the movement runs
        /// alongside the speech, because a robot that finishes moving before it starts talking reads as a
        /// machine executing a script.
        /// </remarks>
        public async Task<string> PerformFromTextAsync(string text, string? name = null)
        {
            var (r, error) = Resolve(name);
            if (r == null) return error!;
            var (_, actions) = SpokenText.Split(text);
            if (actions.Length == 0) return $"{r.Name}: no stage directions in that text.";
            // A robot with motors disabled ACCEPTS every move and stays perfectly still, so this looks
            // exactly like a broken gesture pipeline. Enable first, as the other movement tools do.
            try { await r.Client.SetMotorModeAsync(MotorMode.Enabled); }
            catch (Exception ex) { Console.WriteLine($"[Reachy] enable motors before gesture: {ex.Message}"); }
            r.Body ??= new ReachyBody(r.Client);
            var performed = new List<string>();
            foreach (var a in actions)
            {
                var gesture = GestureClassifier.Classify(a);
                if (gesture == Gesture.None) continue;
                performed.Add($"{gesture} <- \"{Short(a)}\"");
                _ = r.Body.PerformAsync(a, GestureStyle.Default);
            }
            await Task.CompletedTask;
            var result = performed.Count == 0
                ? $"{r.Name}: {actions.Length} stage direction(s), none recognisable as a gesture."
                : $"{r.Name}: performing {string.Join("; ", performed)}";
            return Done("Perform", r, result);
        }

        /// <summary>Turn chat-driven animation on/off and tell Gemini what changed.</summary>
        public async Task<string> SetAnimateFromChatAsync(bool on)
        {
            AnimateFromChat = on;
            try { await _gemini.NotifyToolContext(on ? AnimationBrief : "The robot has stopped acting out your replies; stage directions are no longer needed."); }
            catch (Exception ex) { Console.WriteLine($"[Reachy] animation brief failed: {ex.Message}"); }
            return on
                ? "Gemini's replies will now be acted out by the robot."
                : "Gemini's replies will no longer be acted out.";
        }

        /// <summary>
        /// Open the live WebRTC audio link to the robot and play its microphone through a hidden
        /// <c>&lt;audio&gt;</c> element, so the user can hear the room the robot is in.
        /// </summary>
        /// <remarks>
        /// Only the SIGNALLING is relayed through the background worker (the page cannot open the robot's
        /// plain <c>ws://</c>); the media flows page &lt;-&gt; robot directly, and the decoded track is handed
        /// straight to the audio element. No audio sample ever crosses into .NET - that would be pure
        /// overhead for something the browser already decodes and plays.
        /// Must be called from a user gesture: browsers refuse to start playback without one.
        /// </remarks>
        public async Task<string> ConnectAudioAsync(string? name = null)
        {
            var (r, error) = Resolve(name);
            if (r == null) return error!;
            const string tool = "ConnectAudio";
            if (r.Audio != null) return $"{r.Name}: audio link is already connected ({r.AudioState}).";
            try
            {
                var link = new RoseAudioLink(r.Host, () => new RelayedSignalingSocket(_bes));
                link.Log += m => Console.WriteLine($"[RoseAudio] {m}");
                link.OnConnectionStateChanged += s =>
                {
                    r.AudioState = s;
                    // "failed"/"closed" arrive without any call of ours - reflect them so the UI cannot
                    // keep claiming a link that the browser has already torn down.
                    if (s is "failed" or "closed") _ = DisconnectAudioAsync(r.Name);
                    NotifyChanged();
                };
                // Deliberately NOT subscribing OnMicAudio: that would decode the same track to PCM in .NET
                // for nobody. Speech recognition, if it is ever wanted here, subscribes and the library
                // starts that path on its own.
                link.OnAudioTrack += track => AttachAudioElement(r, track);

                r.Audio = link;                       // set before connecting so a state event has somewhere to land
                await link.ConnectAsync();
                r.Online = true;
                return Done(tool, r, $"{r.Name}: audio link connected ({r.AudioState}); you should hear the robot's microphone.");
            }
            catch (Exception ex)
            {
                await DisconnectAudioAsync(r.Name);
                r.AudioState = Short(ex.Message);
                LogAction(tool, r.Name, "FAILED: " + Short(ex.Message));
                return $"{r.Name}: audio link FAILED - {ex.GetType().Name}: {ex.Message}";
            }
            finally { NotifyChanged(); }
        }

        /// <summary>
        /// Start sending this machine's microphone to the robot's speaker, so the user can talk to it.
        /// Requires an audio link (<see cref="ConnectAudioAsync"/>) and a user gesture - the browser will
        /// prompt for microphone permission on the page's origin the first time.
        /// </summary>
        /// <remarks>
        /// Echo cancellation is requested explicitly rather than left to defaults, because the normal way
        /// to use this is with the robot's microphone playing on the user's LOUDSPEAKERS: without AEC the
        /// captured mic re-sends the robot's own room audio back to it and the loop howls. (The robot's
        /// XVF3800 cancels its OWN output, which is a different problem - it cannot know what our speakers
        /// are doing.) Headphones make it moot; AEC makes it work either way.
        /// </remarks>
        public async Task<string> StartTalkingAsync(string? name = null)
        {
            var (r, error) = Resolve(name);
            if (r == null) return error!;
            const string tool = "StartTalking";
            if (r.Audio == null) return $"{r.Name}: connect the audio link first (Listen), then talk.";
            if (r.MicStream != null) return $"{r.Name}: already sending your microphone.";
            try
            {
                var stream = await RTCMediaDevices.GetUserMedia(new SpawnDev.RTC.MediaStreamConstraints
                {
                    Audio = new SpawnDev.RTC.MediaTrackConstraints
                    {
                        EchoCancellation = true,
                        NoiseSuppression = true,
                        AutoGainControl = true,
                    },
                });
                var track = stream.GetAudioTracks().FirstOrDefault()
                    ?? throw new InvalidOperationException("getUserMedia returned no audio track.");
                await r.Audio.SetSendTrackAsync(track);
                r.MicStream = stream;
                return Done(tool, r, $"{r.Name}: your microphone is now going to the robot's speaker - talk to it.");
            }
            catch (Exception ex)
            {
                try { await StopTalkingAsync(r.Name); } catch { }
                LogAction(tool, r.Name, "FAILED: " + Short(ex.Message));
                return $"{r.Name}: microphone FAILED - {ex.GetType().Name}: {ex.Message}";
            }
            finally { NotifyChanged(); }
        }

        /// <summary>Stop sending the microphone (the audio link stays up, so the robot can still be heard).</summary>
        public async Task<string> StopTalkingAsync(string? name = null)
        {
            var (r, error) = Resolve(name);
            if (r == null) return error!;
            var stream = r.MicStream;
            r.MicStream = null;
            // Clear the sender first so nothing is mid-send when the track stops. replaceTrack(null) needs
            // no renegotiation, so the link itself is undisturbed and Listen keeps working.
            if (r.Audio != null)
            {
                try { await r.Audio.SetSendTrackAsync(null); }
                catch (Exception ex) { Console.WriteLine($"[RoseAudio] clearing send track: {ex.Message}"); }
            }
            if (stream != null)
            {
                // Stop every track, or the browser leaves the mic-in-use indicator on.
                foreach (var t in stream.GetTracks()) { try { t.Stop(); t.Dispose(); } catch { } }
                try { stream.Dispose(); } catch { }
                LogAction("StopTalking", r.Name, $"{r.Name}: microphone stopped.");
            }
            NotifyChanged();
            return stream == null ? $"{r.Name}: microphone was not on." : $"{r.Name}: microphone stopped.";
        }

        /// <summary>Tear down the audio link and stop playback.</summary>
        public async Task<string> DisconnectAudioAsync(string? name = null)
        {
            var (r, error) = Resolve(name);
            if (r == null) return error!;
            // Release the microphone first - dropping the link without stopping the tracks leaves the
            // browser's "in use" indicator on and the device held.
            if (r.MicStream != null) await StopTalkingAsync(r.Name);
            var link = r.Audio;
            var el = r.AudioElement;
            r.Audio = null;
            r.AudioElement = null;
            if (el != null)
            {
                try { el.Pause(); el.SrcObject = null; el.Remove(); } catch (Exception ex) { Console.WriteLine($"[RoseAudio] element teardown: {ex.Message}"); }
                el.Dispose();
            }
            if (link != null)
            {
                try { await link.DisposeAsync(); } catch (Exception ex) { Console.WriteLine($"[RoseAudio] link teardown: {ex.Message}"); }
                r.AudioState = "closed";
                LogAction("DisconnectAudio", r.Name, $"{r.Name}: audio link closed.");
            }
            NotifyChanged();
            return link == null ? $"{r.Name}: no audio link was connected." : $"{r.Name}: audio link closed.";
        }

        /// <summary>
        /// Put the robot's decoded audio track into a hidden autoplay &lt;audio&gt; element.
        /// </summary>
        private void AttachAudioElement(ReachyRobot r, IRTCMediaStreamTrack track)
        {
            try
            {
                // The cross-platform track wraps a real MediaStreamTrack in the browser; srcObject needs
                // that native object inside a MediaStream.
                if (track is not BrowserRTCMediaStreamTrack browserTrack)
                {
                    r.AudioState = $"unexpected track type {track.GetType().Name}";
                    return;
                }
                using var document = _js.Get<Document>("document");
                var el = document.CreateElement<HTMLAudioElement>("audio");
                el.AutoPlay = true;
                el.SetAttribute("data-gemineachy-reachy-audio", r.Name);
                el.SrcObject = new MediaStream(new[] { browserTrack.NativeTrack });
                using var body = document.Body!;
                body.AppendChild(el);
                r.AudioElement = el;
                // autoplay alone is not enough to be sure: report a rejected play() instead of leaving
                // the user with a link that says "connected" and makes no sound.
                _ = el.Play().ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        r.AudioState = "playback blocked: " + Short(t.Exception?.GetBaseException().Message ?? "play() rejected");
                        Console.WriteLine($"[RoseAudio] play() rejected: {t.Exception?.GetBaseException().Message}");
                        NotifyChanged();
                    }
                }, TaskScheduler.Default);
                Console.WriteLine($"[RoseAudio] attached track {track.Id} to <audio>");
            }
            catch (Exception ex)
            {
                r.AudioState = "attach failed: " + Short(ex.Message);
                Console.WriteLine($"[RoseAudio] attach failed: {ex}");
            }
            NotifyChanged();
        }

        private async Task SafeAsync(string name, Func<ReachyMiniClient, Task> action)
        {
            var r = FindRobot(name);
            if (r == null) return;
            try { await action(r.Client); r.Online = true; }
            catch (Exception ex) { r.Online = false; r.Status = Short(ex.Message); }
            NotifyChanged();
        }

        // ---- internals -------------------------------------------------------------------------------

        private (ReachyRobot? robot, string? error) Resolve(string? name)
        {
            if (_robots.Count == 0) return (null, "No robots are configured. Ask the user to add one in the Reachy app.");
            if (string.IsNullOrWhiteSpace(name))
                return _robots.Count == 1 ? (_robots[0], null)
                    : (null, $"Multiple robots are configured - specify one by name: {string.Join(", ", _robots.Select(r => r.Name))}.");
            var robot = FindRobot(name);
            return robot != null ? (robot, null)
                : (null, $"No robot named '{name}'. Configured: {string.Join(", ", _robots.Select(r => r.Name))}.");
        }

        private string Fail(ReachyRobot r, Exception ex, string tool)
        {
            r.Online = false; r.Status = Short(ex.Message);
            var msg = $"{r.Name}: command failed ({Short(ex.Message)}). Is the robot on and reachable at {r.Origin}?";
            LogAction(tool, r.Name, "FAILED: " + Short(ex.Message));
            return msg;
        }

        /// <summary>Enable motors before a physical move (idempotent on the daemon). Without this a move
        /// command is accepted but the robot does not move - the daemon does not auto-enable motors.</summary>
        private static async Task EnsureMotorsAsync(ReachyRobot r)
        {
            try { await r.Client.SetMotorModeAsync(MotorMode.Enabled); }
            catch { /* best effort - the move itself surfaces any real failure */ }
        }

        /// <summary>Safely go limp: first play the go-to-sleep animation so the head is lowered to its
        /// resting pose, wait for it to settle, THEN disable the motors. Disabling from a raised pose would
        /// let the head/antennas drop suddenly.</summary>
        private static async Task SafeDisableMotorsAsync(ReachyRobot r)
        {
            try { await r.Client.GotoSleepAsync(); await Task.Delay(1800); } catch { /* still disable below */ }
            await r.Client.SetMotorModeAsync(MotorMode.Disabled);
        }

        private void RebuildClient(ReachyRobot robot)
        {
            robot.Client?.Dispose();
            var http = new HttpClient(new BackgroundRelayHttpHandler(_bes)) { BaseAddress = new Uri(robot.Origin) };
            robot.Client = new ReachyMiniClient(http, ownsHttp: true);
        }

        private ReachyRobot? FindRobot(string name) => _robots.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

        private string UniqueName(string baseName, ReachyRobot? existingAllowed)
        {
            baseName = baseName.Trim().Replace(' ', '-');
            if (string.IsNullOrEmpty(baseName)) baseName = "reachy";
            var found = FindRobot(baseName);
            if (found == null || found == existingAllowed) return baseName;
            for (int i = 2; ; i++) { var c = $"{baseName}{i}"; if (FindRobot(c) == null) return c; }
        }

        private static string Short(string s) => s.Length <= 100 ? s : s.Substring(0, 100) + "…";

        // ---- Persistence (IndexedDB, mirrors FileSystemService's proven pattern) ----------------------

        private async Task<IDBDatabase> GetDbAsync() => await IDBDatabase.OpenAsync(DB_NAME, 1, evt =>
        {
            using var request = evt.Target;
            using var db = request.Result;
            if (!db.ObjectStoreNames.Contains(STORE)) db.CreateObjectStore<string, string>(STORE);
        });

        private async Task LoadPersistedAsync()
        {
            using var db = await GetDbAsync();
            List<string> names;
            using (var tx = db.Transaction(STORE, false))
            {
                using var store = tx.ObjectStore<string, string>(STORE);
                using var keys = await store.GetAllKeysAsync();
                names = keys.ToList();
            }
            foreach (var name in names)
            {
                string hostPort;
                using (var tx = db.Transaction(STORE, false))
                {
                    using var store = tx.ObjectStore<string, string>(STORE);
                    hostPort = await store.GetAsync(name);
                }
                if (string.IsNullOrWhiteSpace(hostPort)) continue;
                var parts = hostPort.Split(':');
                var host = parts[0];
                var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 8000;
                var robot = new ReachyRobot { Name = name, Host = host, Port = port };
                RebuildClient(robot);
                _robots.Add(robot);
            }
            if (_robots.Count > 0) NotifyChanged();
        }

        private async Task SavePersistedAsync(string name, string hostPort)
        {
            using var db = await GetDbAsync();
            using var tx = db.Transaction(STORE, true);
            using var store = tx.ObjectStore<string, string>(STORE);
            await store.PutAsync(hostPort, name);
        }

        private async Task DeletePersistedAsync(string name)
        {
            using var db = await GetDbAsync();
            using var tx = db.Transaction(STORE, true);
            using var store = tx.ObjectStore<string, string>(STORE);
            await store.DeleteAsync(name);
        }
    }
}
