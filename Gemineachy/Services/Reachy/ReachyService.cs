using SpawnDev;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;
using SpawnDev.SpawnJS.BrowserExtension.Services;
using SpawnDev.Reachy;

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

        public ReachyService(SpawnJSRuntime js, GeminiChatService gemini, BrowserExtensionService bes)
        {
            _js = js;
            _gemini = gemini;
            _bes = bes;
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
