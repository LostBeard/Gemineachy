using SpawnDev;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;
using SpawnDev.SpawnJS.BrowserExtension.Services;
using SpawnDev.Reachy;

namespace Gemineachy.Services.Reachy
{
    /// <summary>
    /// Diagnostic probe (Rule 4c): from the CONTENT side, drive the unchanged <see cref="ReachyMiniClient"/>
    /// through the background relay and capture the RAW outcome of each daemon call - so we can see whether
    /// a "successful" WakeUp actually reaches the daemon and whether motors are enabled (a disabled-motor
    /// robot accepts move commands but does not physically move). Writes a report to data-reachy-probe for
    /// CDP to read. Flip <see cref="RunProbe"/> to run once on startup against the default robot.
    /// </summary>
    public class ReachyRelayProbe : IAsyncBackgroundService
    {
        public Task Ready => _ready ??= InitAsync();
        private Task? _ready;

        // Diagnostic - flip to true to re-run the motors+wake trace against reachy-mini.local on startup.
        // 2026-08-15: found motors=disabled was why WakeUp reached the daemon but the robot didn't move;
        // enabling motors first made it move (TJ confirmed). WakeUp + movement tools now auto-enable motors.
        private const bool RunProbe = false;
        private const string DefaultHost = "reachy-mini.local";

        private readonly SpawnJSRuntime _js;
        private readonly BrowserExtensionService _bes;

        public ReachyRelayProbe(SpawnJSRuntime js, BrowserExtensionService bes)
        {
            _js = js;
            _bes = bes;
        }

        private async Task InitAsync()
        {
#pragma warning disable CS0162 // RunProbe is a const false gate: the ENTIRE body below is
                               // unreachable by design. The restore therefore belongs at the end
                               // of the method - closing it after the guard suppressed nothing.
            if (!RunProbe) return;
            if (_bes.ExtensionMode != ExtensionMode.Content) return;
            await Task.Delay(2000);
            var log = new System.Text.StringBuilder();
            try
            {
                var http = new HttpClient(new BackgroundRelayHttpHandler(_bes)) { BaseAddress = new Uri($"http://{DefaultHost}:8000") };
                using var client = new ReachyMiniClient(http, ownsHttp: true);

                var status = await client.GetStatusAsync();
                log.Append($"status={status?.State}/{status?.Version}; ");

                var m0 = await Try(async () => (await client.GetMotorStatusAsync())?.Mode ?? "null");
                log.Append($"motorsBefore={m0}; ");

                var setEnabled = await Try(async () => { await client.SetMotorModeAsync(MotorMode.Enabled); return "ok"; });
                log.Append($"setEnabled={setEnabled}; ");

                var m1 = await Try(async () => (await client.GetMotorStatusAsync())?.Mode ?? "null");
                log.Append($"motorsAfter={m1}; ");

                var wake = await Try(async () => (await client.WakeUpAsync())?.Uuid ?? "null-handle");
                log.Append($"wakeHandle={wake}; ");

                var m2 = await Try(async () => (await client.GetMotorStatusAsync())?.Mode ?? "null");
                log.Append($"motorsAfterWake={m2}");
            }
            catch (Exception ex)
            {
                log.Append($"THREW {ex.GetType().Name}: {ex.Message}");
            }
            var report = log.ToString();
            Console.WriteLine($"[ReachyProbe] {report}");
            try
            {
                using var document = _js.Get<Document>("document");
                using var docEl = document.DocumentElement!;
                docEl.SetAttribute("data-reachy-probe", report);
            }
            catch (Exception ex) { Console.WriteLine($"[ReachyProbe] DOM marker failed: {ex.Message}"); }
#pragma warning restore CS0162
        }

        private static async Task<string> Try(Func<Task<string>> action)
        {
            try { return await action(); }
            catch (Exception ex) { return $"ERR({ex.GetType().Name}:{Short(ex.Message)})"; }
        }
        private static string Short(string s) => s.Length <= 60 ? s : s.Substring(0, 60) + "…";
    }
}
